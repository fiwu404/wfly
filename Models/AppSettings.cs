using WFly.Services;

namespace WFly.Models;

internal sealed class AppSettings
{
    public string SelectedCoreId { get; set; } = "sing-box";

    /// <summary>
    /// Legacy single native configuration path retained only so older state
    /// files can be migrated into <see cref="NativeConfigPaths"/>.
    /// </summary>
    public string? ConfigPath { get; set; }

    /// <summary>
    /// Imported native configuration paths, keyed by core ID. Keeping Mihomo
    /// and Xray-core separate prevents a generated sing-box runtime profile
    /// or another core's configuration from replacing the user's native file.
    /// </summary>
    public Dictionary<string, string> NativeConfigPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SelectedNodeGroupId { get; set; }
    public string? SelectedNodeId { get; set; }
    public string? SelectedRuleSetId { get; set; }

    /// <summary>
    /// The three home-page operating modes: system proxy, off, and TUN.
    /// Off is the safe default for a new installation.
    /// </summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.Off;

    /// <summary>Local mixed HTTP/SOCKS listener port for generated profiles.</summary>
    public int MixedProxyPort { get; set; } = 2080;

    /// <summary>Requested TUN interface name when TUN mode is selected.</summary>
    public string TunInterfaceName { get; set; } = "WFly";

    public bool ConnectionLoggingEnabled { get; set; } = true;
    public DateTimeOffset? LastExitIpCheckAt { get; set; }

    /// <summary>
    /// Original WinINet settings captured immediately before WFly explicitly
    /// enabled its loopback system proxy. It is used only for a conditional
    /// restore, never to overwrite a user change made in the meantime.
    /// </summary>
    public WindowsSystemProxyLease? SystemProxyLease { get; set; }
}
