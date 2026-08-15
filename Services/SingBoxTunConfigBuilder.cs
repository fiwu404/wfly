using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WFly.Services;

/// <summary>
/// Adds a conservative, documented TUN inbound to a sing-box JSON
/// configuration. It only builds JSON; it never elevates, starts a process, or
/// changes network settings itself.
/// </summary>
/// <remarks>
/// The emitted fields follow the current sing-box TUN inbound reference:
/// https://sing-box.sagernet.org/configuration/inbound/tun/ . The builder
/// requires an elevated Windows process because the generated inbound uses
/// <c>auto_route</c> and needs Windows to configure the TUN interface.
/// </remarks>
internal sealed class SingBoxTunConfigBuilder
{
    /// <summary>
    /// Compatibility entry point for profile generators that already own a
    /// parsed JSON object. The source object is only replaced after all
    /// validation (including the elevation check) succeeds.
    /// </summary>
    public static void AddTunInbound(JsonObject singBoxConfig, string? interfaceName = null)
    {
        ArgumentNullException.ThrowIfNull(singBoxConfig);

        var options = new SingBoxTunOptions
        {
            InterfaceName = string.IsNullOrWhiteSpace(interfaceName)
                ? "WFly"
                : interfaceName.Trim(),
        };
        var result = new SingBoxTunConfigBuilder().TryAddTunInbound(
            singBoxConfig.ToJsonString(),
            options);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.ConfigJson))
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "无法添加 sing-box TUN 入站配置。");
        }

        var updated = JsonNode.Parse(result.ConfigJson)?.AsObject()
            ?? throw new InvalidOperationException("The generated sing-box TUN configuration is invalid.");
        singBoxConfig.Clear();
        foreach (var property in updated)
        {
            singBoxConfig[property.Key] = property.Value?.DeepClone();
        }
    }

    /// <summary>
    /// Returns whether the current Windows process is elevated. No elevation is
    /// requested when this returns false.
    /// </summary>
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Validates, then returns a copy of <paramref name="singBoxConfigJson"/>
    /// with one TUN inbound added. A non-administrator receives a failed result
    /// instead of an elevation prompt or a partially changed configuration.
    /// </summary>
    public SingBoxTunBuildResult TryAddTunInbound(
        string singBoxConfigJson,
        SingBoxTunOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return SingBoxTunBuildResult.Failure("TUN 模式仅可在 Windows 版本中使用。");
        }

        if (!IsAdministrator())
        {
            return SingBoxTunBuildResult.Failure(
                "TUN 模式需要管理员权限。请确认 Windows 的管理员授权提示后重试。");
        }

        if (string.IsNullOrWhiteSpace(singBoxConfigJson))
        {
            return SingBoxTunBuildResult.Failure("A sing-box JSON configuration is required before TUN can be added.");
        }

        options ??= new SingBoxTunOptions();
        if (!TryValidateOptions(options, out var validationError))
        {
            return SingBoxTunBuildResult.Failure(validationError);
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(
                singBoxConfigJson,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 128,
                }) as JsonObject
                ?? throw new JsonException("The configuration root must be an object.");
        }
        catch (JsonException exception)
        {
            return SingBoxTunBuildResult.Failure($"The sing-box configuration is not valid JSON: {exception.Message}");
        }

        if (!TryGetOrCreateInboundArray(root, out var inbounds, out var inboundError))
        {
            return SingBoxTunBuildResult.Failure(inboundError);
        }

        if (!TryEnsureSafeAutoRoute(root, options.AutoRoute, out var routeError))
        {
            return SingBoxTunBuildResult.Failure(routeError);
        }

        foreach (var inboundNode in inbounds)
        {
            if (inboundNode is not JsonObject existingInbound)
            {
                return SingBoxTunBuildResult.Failure("The existing sing-box inbounds array contains a non-object entry.");
            }

            if (HasStringValue(existingInbound, "tag", options.Tag))
            {
                return SingBoxTunBuildResult.Failure(
                    $"The sing-box configuration already contains an inbound tagged '{options.Tag}'.");
            }

            if (HasStringValue(existingInbound, "type", "tun"))
            {
                return SingBoxTunBuildResult.Failure(
                    "The sing-box configuration already contains a TUN inbound; WFly will not replace it.");
            }
        }

        var addresses = new JsonArray
        {
            options.Ipv4Address,
        };
        if (options.EnableIpv6)
        {
            addresses.Add(options.Ipv6Address);
        }

        var tunInbound = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = options.Tag,
            ["interface_name"] = options.InterfaceName,
            ["address"] = addresses,
            ["mtu"] = options.Mtu,
            ["auto_route"] = options.AutoRoute,
            ["strict_route"] = options.StrictRoute,
            ["stack"] = options.Stack,
            ["sniff"] = options.Sniff,
        };

        inbounds.Add(tunInbound);
        return SingBoxTunBuildResult.Success(root.ToJsonString(JsonStore.IndentedOptions));
    }

    private static bool TryGetOrCreateInboundArray(
        JsonObject root,
        out JsonArray inbounds,
        out string error)
    {
        error = string.Empty;
        if (!root.TryGetPropertyValue("inbounds", out var inboundNode) || inboundNode is null)
        {
            inbounds = new JsonArray();
            root["inbounds"] = inbounds;
            return true;
        }

        if (inboundNode is JsonArray array)
        {
            inbounds = array;
            return true;
        }

        inbounds = null!;
        error = "The sing-box 'inbounds' property must be an array.";
        return false;
    }

    private static bool TryEnsureSafeAutoRoute(JsonObject root, bool autoRoute, out string error)
    {
        error = string.Empty;
        if (!autoRoute)
        {
            return true;
        }

        if (!root.TryGetPropertyValue("route", out var routeNode) || routeNode is null)
        {
            root["route"] = new JsonObject { ["auto_detect_interface"] = true };
            return true;
        }

        if (routeNode is not JsonObject route)
        {
            error = "The sing-box 'route' property must be an object when TUN auto_route is enabled.";
            return false;
        }

        if (!route.TryGetPropertyValue("auto_detect_interface", out var autoDetectNode))
        {
            route["auto_detect_interface"] = true;
            return true;
        }

        if (autoDetectNode is JsonValue value && value.TryGetValue<bool>(out var autoDetect) && autoDetect)
        {
            return true;
        }

        error = "TUN auto_route requires route.auto_detect_interface to be true so the core's own traffic does not loop into the TUN interface.";
        return false;
    }

    private static bool HasStringValue(JsonObject node, string propertyName, string expectedValue) =>
        node[propertyName] is JsonValue value &&
        value.TryGetValue<string>(out var actualValue) &&
        string.Equals(actualValue, expectedValue, StringComparison.Ordinal);

    private static bool TryValidateOptions(SingBoxTunOptions options, out string error)
    {
        if (!IsSafeTag(options.Tag))
        {
            error = "The sing-box TUN tag must contain between 1 and 128 printable characters.";
            return false;
        }

        if (!IsSafeInterfaceName(options.InterfaceName))
        {
            error = "The TUN interface name must contain between 1 and 64 characters and cannot contain Windows-reserved filename characters.";
            return false;
        }

        if (!IsValidCidr(options.Ipv4Address, AddressFamily.InterNetwork, minimumPrefix: 1, maximumPrefix: 30))
        {
            error = "The TUN IPv4 address must be a valid IPv4 CIDR with a prefix from /1 through /30.";
            return false;
        }

        if (options.EnableIpv6 &&
            !IsValidCidr(options.Ipv6Address, AddressFamily.InterNetworkV6, minimumPrefix: 1, maximumPrefix: 126))
        {
            error = "The TUN IPv6 address must be a valid IPv6 CIDR with a prefix from /1 through /126.";
            return false;
        }

        if (options.Mtu is < 576 or > 65535)
        {
            error = "The TUN MTU must be between 576 and 65535.";
            return false;
        }

        if (!string.Equals(options.Stack, "system", StringComparison.Ordinal) &&
            !string.Equals(options.Stack, "gvisor", StringComparison.Ordinal) &&
            !string.Equals(options.Stack, "mixed", StringComparison.Ordinal))
        {
            error = "The TUN stack must be system, gvisor, or mixed.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSafeTag(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => !char.IsControl(character));

    private static bool IsSafeInterfaceName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character => !char.IsControl(character) && character is not '\\' and not '/' and not ':' and not '*' and not '?' and not '"' and not '<' and not '>' and not '|');

    private static bool IsValidCidr(
        string? value,
        AddressFamily requiredAddressFamily,
        int minimumPrefix,
        int maximumPrefix)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separatorIndex = value.LastIndexOf('/');
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1 ||
            !int.TryParse(value[(separatorIndex + 1)..], out var prefixLength) ||
            prefixLength < minimumPrefix || prefixLength > maximumPrefix)
        {
            return false;
        }

        return IPAddress.TryParse(value[..separatorIndex], out var address) &&
               address.AddressFamily == requiredAddressFamily;
    }
}

/// <summary>
/// The small, deliberately bounded set of sing-box TUN fields WFly generates.
/// </summary>
internal sealed class SingBoxTunOptions
{
    public string Tag { get; init; } = "tun-in";
    public string InterfaceName { get; init; } = "WFly";
    public string Ipv4Address { get; init; } = "172.18.0.1/30";
    public bool EnableIpv6 { get; init; } = true;
    public string Ipv6Address { get; init; } = "fdfe:dcba:9876::1/126";
    public int Mtu { get; init; } = 9000;
    public bool AutoRoute { get; init; } = true;
    public bool StrictRoute { get; init; } = true;
    public string Stack { get; init; } = "system";
    public bool Sniff { get; init; } = true;
}

internal sealed record SingBoxTunBuildResult(bool Succeeded, string? ConfigJson, string? ErrorMessage)
{
    public static SingBoxTunBuildResult Success(string configJson) => new(true, configJson, null);

    public static SingBoxTunBuildResult Failure(string errorMessage) => new(false, null, errorMessage);
}
