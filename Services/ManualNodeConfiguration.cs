using System.Text.Json;
using System.Text.Json.Nodes;

namespace WFly.Services;

/// <summary>
/// Lossless field model for the v2rayN-style manual-node editor. Values that
/// sing-box supports are rendered into an outbound; the complete model is
/// stored separately so switching protocols or cores does not discard input.
/// </summary>
internal sealed class ManualNodeOptions
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 443;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string VmessSecurity { get; set; } = "auto";
    public int AlterId { get; set; }
    public string VlessEncryption { get; set; } = "none";
    public string Flow { get; set; } = string.Empty;
    public string ShadowsocksMethod { get; set; } = "aes-256-gcm";
    public string Plugin { get; set; } = string.Empty;
    public string PluginOptions { get; set; } = string.Empty;
    public string SocksVersion { get; set; } = "5";
    public string HttpHeadersJson { get; set; } = string.Empty;
    public bool UdpOverTcp { get; set; }

    public string ServerPorts { get; set; } = string.Empty;
    public string HopInterval { get; set; } = string.Empty;
    public int? UpMbps { get; set; }
    public int? DownMbps { get; set; }
    public string ObfsType { get; set; } = "salamander";
    public string ObfsPassword { get; set; } = string.Empty;
    public string HysteriaRealmUrl { get; set; } = string.Empty;
    public int? GeckoMinPacketSize { get; set; }
    public int? GeckoMaxPacketSize { get; set; }

    public string CongestionControl { get; set; } = "bbr";
    public string UdpRelayMode { get; set; } = "native";
    public bool ZeroRttHandshake { get; set; }
    public string Heartbeat { get; set; } = string.Empty;

    public string WireGuardPublicKey { get; set; } = string.Empty;
    public string WireGuardPreSharedKey { get; set; } = string.Empty;
    public string WireGuardLocalAddress { get; set; } = string.Empty;
    public string WireGuardReserved { get; set; } = string.Empty;
    public int WireGuardMtu { get; set; } = 1280;

    public string IdleSessionCheckInterval { get; set; } = string.Empty;
    public string IdleSessionTimeout { get; set; } = string.Empty;
    public int? MinIdleSession { get; set; }
    public int? InsecureConcurrency { get; set; }
    public bool NaiveQuic { get; set; }

    public string Network { get; set; } = "raw";
    public string HeaderType { get; set; } = "none";
    public string Host { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string TransportMode { get; set; } = string.Empty;
    public string TransportExtra { get; set; } = string.Empty;
    public int? KcpMtu { get; set; }
    public bool MuxEnabled { get; set; }

    public string TlsSecurity { get; set; } = "none";
    public bool AllowInsecure { get; set; }
    public string Sni { get; set; } = string.Empty;
    public string Alpn { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string RealityPublicKey { get; set; } = string.Empty;
    public string RealityShortId { get; set; } = string.Empty;
    public string RealitySpiderX { get; set; } = string.Empty;
    public string Mldsa65Verify { get; set; } = string.Empty;
    public string Certificate { get; set; } = string.Empty;
    public string CertificateSha256 { get; set; } = string.Empty;
    public string EchConfigList { get; set; } = string.Empty;
    public string VerifyPeerCertificateByName { get; set; } = string.Empty;
    public string FinalMask { get; set; } = string.Empty;
}

internal static class ManualNodeConfiguration
{
    public static readonly string[] Protocols =
    [
        "VMess", "VLESS", "Shadowsocks", "Trojan", "Hysteria2", "TUIC",
        "WireGuard", "SOCKS", "HTTP", "AnyTLS", "Naive", "sing-box 自定义出站"
    ];

    public static ManualNodeOptions Load(string? manualOptionsJson, string? outboundJson)
    {
        if (!string.IsNullOrWhiteSpace(manualOptionsJson))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<ManualNodeOptions>(manualOptionsJson, JsonStore.Options);
                if (saved is not null)
                {
                    return saved;
                }
            }
            catch (JsonException)
            {
                // Fall back to the runnable outbound when an old draft is malformed.
            }
        }

        return ParseOutbound(outboundJson);
    }

    public static string Serialize(ManualNodeOptions options) =>
        JsonSerializer.Serialize(options, JsonStore.Options);

    public static string Build(string protocol, string tag, ManualNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var type = ToOutboundType(protocol);
        if (type is null)
        {
            throw new InvalidDataException("自定义出站必须在“导入与高级”页填写有效 JSON。");
        }

        var server = Required(options.Server, "服务器地址");
        if (options.Port is <= 0 or >= 65536)
        {
            throw new InvalidDataException("服务器端口必须介于 1 和 65535 之间。");
        }

        var outbound = new JsonObject
        {
            ["type"] = type,
            ["tag"] = string.IsNullOrWhiteSpace(tag) ? "proxy" : tag.Trim(),
            ["server"] = server,
            ["server_port"] = options.Port,
        };

        switch (type)
        {
            case "vmess":
                outbound["uuid"] = Required(options.Password, "UUID");
                outbound["security"] = Default(options.VmessSecurity, "auto");
                outbound["alter_id"] = Math.Max(0, options.AlterId);
                ApplyV2RayTransport(outbound, options);
                ApplyTls(outbound, options, forceEnabled: false);
                ApplyMux(outbound, options);
                break;
            case "vless":
                outbound["uuid"] = Required(options.Password, "UUID");
                if (!string.IsNullOrWhiteSpace(options.Flow)) outbound["flow"] = options.Flow.Trim();
                if (!string.IsNullOrWhiteSpace(options.VlessEncryption) && options.VlessEncryption != "none")
                    outbound["encryption"] = options.VlessEncryption.Trim();
                ApplyV2RayTransport(outbound, options);
                ApplyTls(outbound, options, forceEnabled: false);
                ApplyMux(outbound, options);
                break;
            case "shadowsocks":
                outbound["method"] = Required(options.ShadowsocksMethod, "加密方法");
                outbound["password"] = Required(options.Password, "密码");
                AddText(outbound, "plugin", options.Plugin);
                AddText(outbound, "plugin_opts", options.PluginOptions);
                ApplyUdpOverTcp(outbound, options);
                break;
            case "trojan":
                outbound["password"] = Required(options.Password, "密码");
                ApplyV2RayTransport(outbound, options);
                ApplyTls(outbound, options, forceEnabled: true);
                ApplyMux(outbound, options);
                break;
            case "hysteria2":
                outbound["password"] = Required(options.Password, "密码");
                AddList(outbound, "server_ports", options.ServerPorts);
                AddText(outbound, "hop_interval", options.HopInterval);
                AddPositive(outbound, "up_mbps", options.UpMbps);
                AddPositive(outbound, "down_mbps", options.DownMbps);
                if (!string.IsNullOrWhiteSpace(options.ObfsPassword))
                {
                    outbound["obfs"] = new JsonObject
                    {
                        ["type"] = Default(options.ObfsType, "salamander"),
                        ["password"] = options.ObfsPassword.Trim(),
                    };
                }
                ApplyTls(outbound, options, forceEnabled: true);
                break;
            case "tuic":
                outbound["uuid"] = Required(options.Username, "UUID");
                outbound["password"] = Required(options.Password, "密码");
                outbound["congestion_control"] = Default(options.CongestionControl, "bbr");
                outbound["udp_relay_mode"] = Default(options.UdpRelayMode, "native");
                if (options.ZeroRttHandshake) outbound["zero_rtt_handshake"] = true;
                AddText(outbound, "heartbeat", options.Heartbeat);
                ApplyTls(outbound, options, forceEnabled: true);
                break;
            case "wireguard":
                outbound["private_key"] = Required(options.Password, "私钥");
                outbound["peer_public_key"] = Required(options.WireGuardPublicKey, "对端公钥");
                AddText(outbound, "pre_shared_key", options.WireGuardPreSharedKey);
                AddList(outbound, "local_address", options.WireGuardLocalAddress);
                var reserved = ParseByteList(options.WireGuardReserved);
                if (reserved.Count > 0) outbound["reserved"] = reserved;
                if (options.WireGuardMtu >= 576) outbound["mtu"] = options.WireGuardMtu;
                break;
            case "socks":
                outbound["version"] = Default(options.SocksVersion, "5");
                AddText(outbound, "username", options.Username);
                AddText(outbound, "password", options.Password);
                ApplyUdpOverTcp(outbound, options);
                break;
            case "http":
                AddText(outbound, "username", options.Username);
                AddText(outbound, "password", options.Password);
                AddJsonObject(outbound, "headers", options.HttpHeadersJson, "HTTP 请求头 JSON");
                ApplyTls(outbound, options, forceEnabled: false);
                break;
            case "anytls":
                outbound["password"] = Required(options.Password, "密码");
                AddText(outbound, "idle_session_check_interval", options.IdleSessionCheckInterval);
                AddText(outbound, "idle_session_timeout", options.IdleSessionTimeout);
                AddPositive(outbound, "min_idle_session", options.MinIdleSession);
                ApplyTls(outbound, options, forceEnabled: true);
                break;
            case "naive":
                outbound["username"] = Required(options.Username, "用户名");
                outbound["password"] = Required(options.Password, "密码");
                if (options.NaiveQuic) outbound["network"] = "quic";
                AddText(outbound, "congestion_control", options.CongestionControl);
                AddPositive(outbound, "insecure_concurrency", options.InsecureConcurrency);
                ApplyTls(outbound, options, forceEnabled: true);
                break;
        }

        return outbound.ToJsonString(JsonStore.IndentedOptions);
    }

    public static string? DetectProtocol(string? outboundJson)
    {
        try
        {
            var root = JsonNode.Parse(outboundJson ?? string.Empty) as JsonObject;
            return FromOutboundType(root?["type"]?.GetValue<string>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ManualNodeOptions ParseOutbound(string? outboundJson)
    {
        var options = new ManualNodeOptions();
        if (string.IsNullOrWhiteSpace(outboundJson)) return options;

        try
        {
            if (JsonNode.Parse(outboundJson) is not JsonObject root) return options;
            options.Server = Text(root, "server");
            options.Port = Number(root, "server_port") ?? 443;
            options.Username = Text(root, "username");
            options.Password = Text(root, "password");
            var type = Text(root, "type");
            if (type is "vmess" or "vless") options.Password = Text(root, "uuid");
            if (type == "tuic") options.Username = Text(root, "uuid");
            if (type == "wireguard") options.Password = Text(root, "private_key");

            options.VmessSecurity = Default(Text(root, "security"), "auto");
            options.AlterId = Number(root, "alter_id") ?? 0;
            options.VlessEncryption = Default(Text(root, "encryption"), "none");
            options.Flow = Text(root, "flow");
            options.ShadowsocksMethod = Default(Text(root, "method"), "aes-256-gcm");
            options.Plugin = Text(root, "plugin");
            options.PluginOptions = Text(root, "plugin_opts");
            options.SocksVersion = Default(Text(root, "version"), "5");
            options.ServerPorts = Join(root["server_ports"]);
            options.HopInterval = Text(root, "hop_interval");
            options.UpMbps = Number(root, "up_mbps");
            options.DownMbps = Number(root, "down_mbps");
            options.CongestionControl = Default(Text(root, "congestion_control"), "bbr");
            options.UdpRelayMode = Default(Text(root, "udp_relay_mode"), "native");
            options.ZeroRttHandshake = Boolean(root, "zero_rtt_handshake");
            options.Heartbeat = Text(root, "heartbeat");
            options.WireGuardPublicKey = Text(root, "peer_public_key");
            options.WireGuardPreSharedKey = Text(root, "pre_shared_key");
            options.WireGuardLocalAddress = Join(root["local_address"]);
            options.WireGuardReserved = Join(root["reserved"]);
            options.WireGuardMtu = Number(root, "mtu") ?? 1280;
            options.IdleSessionCheckInterval = Text(root, "idle_session_check_interval");
            options.IdleSessionTimeout = Text(root, "idle_session_timeout");
            options.MinIdleSession = Number(root, "min_idle_session");
            options.InsecureConcurrency = Number(root, "insecure_concurrency");
            options.NaiveQuic = string.Equals(Text(root, "network"), "quic", StringComparison.OrdinalIgnoreCase);
            if (root["headers"] is JsonObject headers) options.HttpHeadersJson = headers.ToJsonString(JsonStore.IndentedOptions);
            if (root["udp_over_tcp"] is JsonObject uot) options.UdpOverTcp = Boolean(uot, "enabled");
            if (root["multiplex"] is JsonObject mux) options.MuxEnabled = Boolean(mux, "enabled");

            if (root["obfs"] is JsonObject obfs)
            {
                options.ObfsType = Default(Text(obfs, "type"), "salamander");
                options.ObfsPassword = Text(obfs, "password");
            }

            ParseTransport(root["transport"] as JsonObject, options);
            ParseTls(root["tls"] as JsonObject, options);
        }
        catch (JsonException)
        {
            // The advanced editor will surface invalid JSON; basic fields keep defaults.
        }
        catch (InvalidOperationException)
        {
            // A valid JSON shape with incompatible scalar types is treated as custom JSON.
        }

        return options;
    }

    private static void ApplyV2RayTransport(JsonObject outbound, ManualNodeOptions options)
    {
        var network = Default(options.Network, "raw").ToLowerInvariant();
        if (network is "raw" or "tcp" or "none")
        {
            if (!string.Equals(options.HeaderType, "http", StringComparison.OrdinalIgnoreCase)) return;
            var http = new JsonObject { ["type"] = "http" };
            AddList(http, "host", options.Host);
            AddText(http, "path", options.Path);
            outbound["transport"] = http;
            return;
        }

        var transport = new JsonObject { ["type"] = network };
        switch (network)
        {
            case "ws":
            case "httpupgrade":
                AddText(transport, "path", options.Path);
                if (!string.IsNullOrWhiteSpace(options.Host))
                    transport["headers"] = new JsonObject { ["Host"] = options.Host.Trim() };
                break;
            case "http":
                AddList(transport, "host", options.Host);
                AddText(transport, "path", options.Path);
                break;
            case "grpc":
                AddText(transport, "service_name", options.Path);
                AddText(transport, "authority", options.Host);
                AddText(transport, "mode", options.TransportMode);
                break;
            case "xhttp":
                AddText(transport, "path", options.Path);
                AddText(transport, "host", options.Host);
                AddText(transport, "mode", options.TransportMode);
                AddJsonObject(transport, "extra", options.TransportExtra, "xHTTP 额外参数 JSON");
                break;
            case "kcp":
                AddText(transport, "header_type", options.HeaderType);
                AddText(transport, "seed", options.Path);
                AddPositive(transport, "mtu", options.KcpMtu);
                break;
        }
        outbound["transport"] = transport;
    }

    private static void ApplyTls(JsonObject outbound, ManualNodeOptions options, bool forceEnabled)
    {
        var mode = Default(options.TlsSecurity, forceEnabled ? "tls" : "none").ToLowerInvariant();
        if (!forceEnabled && mode is "none" or "off" or "") return;

        var tls = new JsonObject { ["enabled"] = true };
        AddText(tls, "server_name", options.Sni);
        if (options.AllowInsecure) tls["insecure"] = true;
        AddList(tls, "alpn", options.Alpn);
        if (!string.IsNullOrWhiteSpace(options.Fingerprint))
        {
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = options.Fingerprint.Trim(),
            };
        }
        if (mode == "reality")
        {
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = Required(options.RealityPublicKey, "Reality 公钥"),
                ["short_id"] = options.RealityShortId.Trim(),
            };
        }
        outbound["tls"] = tls;
    }

    private static void ApplyMux(JsonObject outbound, ManualNodeOptions options)
    {
        if (options.MuxEnabled) outbound["multiplex"] = new JsonObject { ["enabled"] = true };
    }

    private static void ApplyUdpOverTcp(JsonObject outbound, ManualNodeOptions options)
    {
        if (options.UdpOverTcp) outbound["udp_over_tcp"] = new JsonObject { ["enabled"] = true };
    }

    private static void ParseTransport(JsonObject? transport, ManualNodeOptions options)
    {
        if (transport is null) return;
        options.Network = Default(Text(transport, "type"), "raw");
        options.Path = Text(transport, "path");
        options.Host = Text(transport, "host");
        if (transport["headers"] is JsonObject headers) options.Host = Text(headers, "Host");
        if (options.Network == "grpc")
        {
            options.Path = Text(transport, "service_name");
            options.Host = Text(transport, "authority");
        }
        options.HeaderType = Default(Text(transport, "header_type"), "none");
        options.TransportMode = Text(transport, "mode");
        options.TransportExtra = transport["extra"]?.ToJsonString(JsonStore.IndentedOptions) ?? string.Empty;
        options.KcpMtu = Number(transport, "mtu");
    }

    private static void ParseTls(JsonObject? tls, ManualNodeOptions options)
    {
        if (tls is null || !Boolean(tls, "enabled")) return;
        options.TlsSecurity = tls["reality"] is JsonObject ? "reality" : "tls";
        options.AllowInsecure = Boolean(tls, "insecure");
        options.Sni = Text(tls, "server_name");
        options.Alpn = Join(tls["alpn"]);
        if (tls["utls"] is JsonObject utls) options.Fingerprint = Text(utls, "fingerprint");
        if (tls["reality"] is JsonObject reality)
        {
            options.RealityPublicKey = Text(reality, "public_key");
            options.RealityShortId = Text(reality, "short_id");
        }
    }

    private static string? ToOutboundType(string protocol) => protocol.Trim().ToLowerInvariant() switch
    {
        "vmess" => "vmess",
        "vless" => "vless",
        "shadowsocks" => "shadowsocks",
        "trojan" => "trojan",
        "hysteria2" => "hysteria2",
        "tuic" => "tuic",
        "wireguard" => "wireguard",
        "socks" => "socks",
        "http" => "http",
        "anytls" => "anytls",
        "naive" => "naive",
        _ => null,
    };

    private static string? FromOutboundType(string? type) => type?.ToLowerInvariant() switch
    {
        "vmess" => "VMess", "vless" => "VLESS", "shadowsocks" => "Shadowsocks",
        "trojan" => "Trojan", "hysteria2" => "Hysteria2", "tuic" => "TUIC",
        "wireguard" => "WireGuard", "socks" => "SOCKS", "http" => "HTTP",
        "anytls" => "AnyTLS", "naive" => "Naive", _ => null,
    };

    private static string Required(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"请填写{fieldName}。")
            : value.Trim();

    private static string Default(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void AddText(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[name] = value.Trim();
    }

    private static void AddPositive(JsonObject target, string name, int? value)
    {
        if (value > 0) target[name] = value.Value;
    }

    private static void AddList(JsonObject target, string name, string? value)
    {
        var values = Split(value);
        if (values.Count == 0) return;
        var array = new JsonArray();
        foreach (var item in values) array.Add(item);
        target[name] = array;
    }

    private static void AddJsonObject(JsonObject target, string name, string? json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            target[name] = JsonNode.Parse(json) is JsonObject value
                ? value
                : throw new InvalidDataException($"{fieldName}必须是 JSON 对象。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{fieldName}无效：{exception.Message}", exception);
        }
    }

    private static JsonArray ParseByteList(string? text)
    {
        var result = new JsonArray();
        foreach (var item in Split(text))
        {
            if (!byte.TryParse(item, out var value))
                throw new InvalidDataException("WireGuard Reserved 只能包含 0 到 255 的整数。");
            result.Add(value);
        }
        return result;
    }

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Join(JsonNode? node)
    {
        if (node is not JsonArray array) return node?.ToString() ?? string.Empty;
        return string.Join(",", array.Select(static item => item?.ToString()).Where(static item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string Text(JsonObject root, string name)
    {
        var node = root[name];
        if (node is null) return string.Empty;
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToString();
    }

    private static int? Number(JsonObject root, string name)
    {
        var node = root[name];
        if (node is JsonValue value && value.TryGetValue<int>(out var number)) return number;
        return int.TryParse(node?.ToString(), out number) ? number : null;
    }

    private static bool Boolean(JsonObject root, string name)
    {
        var node = root[name];
        if (node is JsonValue value && value.TryGetValue<bool>(out var result)) return result;
        return bool.TryParse(node?.ToString(), out result) && result;
    }
}
