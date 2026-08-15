using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace WFly.Services;

/// <summary>
/// Applies a local WinINet proxy for the current Windows user and restores the
/// exact settings that were present before WFly changed them.
/// </summary>
/// <remarks>
/// <para>
/// This service intentionally has no generic "set proxy" API. The only
/// endpoint it can apply is <c>127.0.0.1:&lt;port&gt;</c>, which keeps a caller
/// from accidentally redirecting the system through an arbitrary remote host.
/// </para>
/// <para>
/// Callers should persist the returned <see cref="WindowsSystemProxyLease"/>
/// with their own application state. When restoring, this service first checks
/// that the current settings are still exactly those WFly applied. If another
/// application or the user changed them, it leaves the settings untouched.
/// </para>
/// </remarks>
internal sealed class WindowsSystemProxyService
{
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    /// <summary>
    /// Captures the WinINet values that control manual and automatic proxy
    /// selection for the current user. No registry values are changed.
    /// </summary>
    public WindowsSystemProxySnapshot CaptureSettings()
    {
        EnsureWindows();

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: false);
        return new WindowsSystemProxySnapshot
        {
            ProxyEnable = ReadValue(key, "ProxyEnable"),
            ProxyServer = ReadValue(key, "ProxyServer"),
            ProxyOverride = ReadValue(key, "ProxyOverride"),
            AutoConfigUrl = ReadValue(key, "AutoConfigURL"),
            AutoDetect = ReadValue(key, "AutoDetect"),
        };
    }

    /// <summary>
    /// Explicitly selects a loopback HTTP proxy. Automatic discovery and a PAC
    /// URL are disabled only for the duration of the returned lease, so Windows
    /// will consistently use the selected local proxy.
    /// </summary>
    /// <param name="port">The local listener port, from 1 through 65535.</param>
    /// <param name="existingLease">
    /// A lease previously returned by this service. Supplying it when switching
    /// a WFly listener to a new port preserves the original pre-WFly snapshot.
    /// </param>
    public WindowsSystemProxyApplyResult ApplyLoopbackProxy(
        int port,
        WindowsSystemProxyLease? existingLease = null)
    {
        EnsureWindows();
        ValidatePort(port);

        var current = CaptureSettings();
        var original = existingLease is not null &&
                       current.EquivalentTo(existingLease.AppliedSettings)
            ? existingLease.OriginalSettings
            : current;

        var applied = BuildLoopbackSettings(current, port);
        ReplaceSettingsIfUnchanged(current, applied);

        var lease = new WindowsSystemProxyLease(original, applied, port);
        return new WindowsSystemProxyApplyResult(lease, NotifySettingsChanged());
    }

    /// <summary>
    /// Restores the settings captured in <paramref name="lease"/> only when
    /// the current values still match that lease's WFly-owned values.
    /// </summary>
    public WindowsSystemProxyRestoreResult RestoreIfOwned(WindowsSystemProxyLease? lease)
    {
        EnsureWindows();

        if (lease is null)
        {
            return new WindowsSystemProxyRestoreResult(
                WindowsSystemProxyRestoreStatus.NoLease,
                SettingsRefreshNotified: false);
        }

        var current = CaptureSettings();
        if (!current.EquivalentTo(lease.AppliedSettings))
        {
            return new WindowsSystemProxyRestoreResult(
                WindowsSystemProxyRestoreStatus.CurrentSettingsChanged,
                SettingsRefreshNotified: false);
        }

        ReplaceSettingsIfUnchanged(current, lease.OriginalSettings);
        return new WindowsSystemProxyRestoreResult(
            WindowsSystemProxyRestoreStatus.Restored,
            NotifySettingsChanged());
    }

    private static WindowsSystemProxySnapshot BuildLoopbackSettings(
        WindowsSystemProxySnapshot current,
        int port)
    {
        return new WindowsSystemProxySnapshot
        {
            ProxyEnable = WindowsRegistryValueSnapshot.FromDword(1),
            ProxyServer = WindowsRegistryValueSnapshot.FromString($"127.0.0.1:{port}"),
            // A bypass list is not WFly-owned, so it is preserved verbatim.
            ProxyOverride = current.ProxyOverride,
            // PAC and automatic detection can override a manual proxy. They are
            // restored verbatim when the caller releases the lease.
            AutoConfigUrl = WindowsRegistryValueSnapshot.Missing,
            AutoDetect = WindowsRegistryValueSnapshot.FromDword(0),
        };
    }

    private void ReplaceSettingsIfUnchanged(
        WindowsSystemProxySnapshot expectedCurrent,
        WindowsSystemProxySnapshot desired)
    {
        // Check immediately before writing. This deliberately fails rather than
        // overwriting a concurrent user or policy change.
        var observed = CaptureSettings();
        if (!observed.EquivalentTo(expectedCurrent))
        {
            throw new InvalidOperationException(
                "Windows proxy settings changed before WFly could update them. Please try the requested action again.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the current user's WinINet settings.");

        var written = new List<string>();
        try
        {
            foreach (var setting in desired.EnumerateSettings())
            {
                WriteValue(key, setting.Name, setting.Value);
                written.Add(setting.Name);
            }
        }
        catch
        {
            // Best-effort rollback of only values that still equal WFly's
            // attempted value. This avoids clobbering a concurrent edit.
            foreach (var settingName in written.AsEnumerable().Reverse())
            {
                var currentValue = ReadValue(key, settingName);
                var desiredValue = desired.GetSetting(settingName);
                if (currentValue.EquivalentTo(desiredValue))
                {
                    try
                    {
                        WriteValue(key, settingName, expectedCurrent.GetSetting(settingName));
                    }
                    catch (IOException)
                    {
                        // The original exception remains the useful error.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // See the comment above.
                    }
                }
            }

            throw;
        }
    }

    private static WindowsRegistryValueSnapshot ReadValue(RegistryKey? key, string name)
    {
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return WindowsRegistryValueSnapshot.Missing;
        }

        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return WindowsRegistryValueSnapshot.FromRegistryValue(kind, value);
    }

    private static void WriteValue(RegistryKey key, string name, WindowsRegistryValueSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        key.SetValue(name, snapshot.ToRegistryValue(), snapshot.Kind);
    }

    private static bool NotifySettingsChanged()
    {
        var changed = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        var refreshed = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        return changed && refreshed;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows system proxy settings are only available on Windows.");
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The local proxy port must be between 1 and 65535.");
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);
}

/// <summary>
/// A serializable snapshot of exactly the WinINet values WFly may change.
/// </summary>
internal sealed class WindowsSystemProxySnapshot
{
    public WindowsRegistryValueSnapshot ProxyEnable { get; init; } = WindowsRegistryValueSnapshot.Missing;
    public WindowsRegistryValueSnapshot ProxyServer { get; init; } = WindowsRegistryValueSnapshot.Missing;
    public WindowsRegistryValueSnapshot ProxyOverride { get; init; } = WindowsRegistryValueSnapshot.Missing;
    public WindowsRegistryValueSnapshot AutoConfigUrl { get; init; } = WindowsRegistryValueSnapshot.Missing;
    public WindowsRegistryValueSnapshot AutoDetect { get; init; } = WindowsRegistryValueSnapshot.Missing;

    internal bool EquivalentTo(WindowsSystemProxySnapshot? other) =>
        other is not null &&
        ProxyEnable.EquivalentTo(other.ProxyEnable) &&
        ProxyServer.EquivalentTo(other.ProxyServer) &&
        ProxyOverride.EquivalentTo(other.ProxyOverride) &&
        AutoConfigUrl.EquivalentTo(other.AutoConfigUrl) &&
        AutoDetect.EquivalentTo(other.AutoDetect);

    internal IEnumerable<(string Name, WindowsRegistryValueSnapshot Value)> EnumerateSettings()
    {
        yield return ("ProxyEnable", ProxyEnable);
        yield return ("ProxyServer", ProxyServer);
        yield return ("ProxyOverride", ProxyOverride);
        yield return ("AutoConfigURL", AutoConfigUrl);
        yield return ("AutoDetect", AutoDetect);
    }

    internal WindowsRegistryValueSnapshot GetSetting(string name) => name switch
    {
        "ProxyEnable" => ProxyEnable,
        "ProxyServer" => ProxyServer,
        "ProxyOverride" => ProxyOverride,
        "AutoConfigURL" => AutoConfigUrl,
        "AutoDetect" => AutoDetect,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}

/// <summary>
/// Captures one registry value without serializing arbitrary runtime objects.
/// The supported forms cover all WinINet values this service reads.
/// </summary>
internal sealed class WindowsRegistryValueSnapshot
{
    public static WindowsRegistryValueSnapshot Missing { get; } = new();

    public bool Exists { get; init; }
    public RegistryValueKind Kind { get; init; } = RegistryValueKind.Unknown;
    public string? TextValue { get; init; }
    public int? DwordValue { get; init; }
    public long? QwordValue { get; init; }
    public string? BinaryValueBase64 { get; init; }
    public string[]? MultiStringValue { get; init; }

    public static WindowsRegistryValueSnapshot FromDword(int value) => new()
    {
        Exists = true,
        Kind = RegistryValueKind.DWord,
        DwordValue = value,
    };

    public static WindowsRegistryValueSnapshot FromString(string value) => new()
    {
        Exists = true,
        Kind = RegistryValueKind.String,
        TextValue = value,
    };

    internal static WindowsRegistryValueSnapshot FromRegistryValue(RegistryValueKind kind, object? value) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => new WindowsRegistryValueSnapshot
        {
            Exists = true,
            Kind = kind,
            TextValue = value as string ?? throw UnexpectedRegistryValue(kind),
        },
        RegistryValueKind.DWord => new WindowsRegistryValueSnapshot
        {
            Exists = true,
            Kind = kind,
            DwordValue = value is not null ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : throw UnexpectedRegistryValue(kind),
        },
        RegistryValueKind.QWord => new WindowsRegistryValueSnapshot
        {
            Exists = true,
            Kind = kind,
            QwordValue = value is not null ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : throw UnexpectedRegistryValue(kind),
        },
        RegistryValueKind.Binary => new WindowsRegistryValueSnapshot
        {
            Exists = true,
            Kind = kind,
            BinaryValueBase64 = value is byte[] bytes
                ? Convert.ToBase64String(bytes)
                : throw UnexpectedRegistryValue(kind),
        },
        RegistryValueKind.MultiString => new WindowsRegistryValueSnapshot
        {
            Exists = true,
            Kind = kind,
            MultiStringValue = value is string[] strings
                ? strings.ToArray()
                : throw UnexpectedRegistryValue(kind),
        },
        _ => throw new InvalidOperationException(
            $"WinINet registry value has unsupported registry type '{kind}', so WFly will not modify it."),
    };

    internal object ToRegistryValue()
    {
        if (!Exists)
        {
            throw new InvalidOperationException("A missing registry value cannot be written.");
        }

        return Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => TextValue
                ?? throw UnexpectedRegistryValue(Kind),
            RegistryValueKind.DWord => DwordValue
                ?? throw UnexpectedRegistryValue(Kind),
            RegistryValueKind.QWord => QwordValue
                ?? throw UnexpectedRegistryValue(Kind),
            RegistryValueKind.Binary => BinaryValueBase64 is not null
                ? Convert.FromBase64String(BinaryValueBase64)
                : throw UnexpectedRegistryValue(Kind),
            RegistryValueKind.MultiString => MultiStringValue?.ToArray()
                ?? throw UnexpectedRegistryValue(Kind),
            _ => throw new InvalidOperationException(
                $"WinINet registry value has unsupported registry type '{Kind}', so WFly will not modify it."),
        };
    }

    internal bool EquivalentTo(WindowsRegistryValueSnapshot? other)
    {
        if (other is null || Exists != other.Exists)
        {
            return false;
        }

        if (!Exists)
        {
            return true;
        }

        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString =>
                string.Equals(TextValue, other.TextValue, StringComparison.Ordinal),
            RegistryValueKind.DWord => DwordValue == other.DwordValue,
            RegistryValueKind.QWord => QwordValue == other.QwordValue,
            RegistryValueKind.Binary =>
                string.Equals(BinaryValueBase64, other.BinaryValueBase64, StringComparison.Ordinal),
            RegistryValueKind.MultiString =>
                (MultiStringValue ?? []).SequenceEqual(other.MultiStringValue ?? [], StringComparer.Ordinal),
            _ => false,
        };
    }

    private static InvalidOperationException UnexpectedRegistryValue(RegistryValueKind kind) =>
        new($"The WinINet registry value is not valid for registry type '{kind}'.");
}

/// <summary>
/// The original and WFly-applied values needed for a safe, conditional restore.
/// </summary>
internal sealed record WindowsSystemProxyLease(
    WindowsSystemProxySnapshot OriginalSettings,
    WindowsSystemProxySnapshot AppliedSettings,
    int LoopbackPort);

internal sealed record WindowsSystemProxyApplyResult(
    WindowsSystemProxyLease Lease,
    bool SettingsRefreshNotified);

internal enum WindowsSystemProxyRestoreStatus
{
    Restored,
    NoLease,
    CurrentSettingsChanged,
}

internal sealed record WindowsSystemProxyRestoreResult(
    WindowsSystemProxyRestoreStatus Status,
    bool SettingsRefreshNotified);
