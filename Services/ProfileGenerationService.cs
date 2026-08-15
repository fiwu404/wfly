using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Renders the selected durable node and graphical rules to a fresh local
/// sing-box profile. User-authored source nodes are never modified in place;
/// generated runtime files live under data/profiles and can be regenerated.
/// </summary>
internal sealed class ProfileGenerationService
{
    private static readonly HashSet<string> SupportedSingBoxOutboundTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Keep this to sing-box's documented outbound types. Subscription
        // parsing only produces a small safe subset; advanced entries remain
        // local, user-authored outbound JSON and still receive a fresh tag.
        "direct", "bridge", "block", "socks", "http", "shadowsocks",
        "vmess", "trojan", "wireguard", "hysteria", "vless", "shadowtls",
        "tuic", "hysteria2", "anytls", "snell", "tor", "ssh", "dns",
        "selector", "urltest", "naive",
    };

    private readonly AppPaths _paths;

    public ProfileGenerationService(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<GeneratedProfile> GenerateSingBoxAsync(
        ProxyNode selectedNode,
        IEnumerable<RuleSet> ruleSets,
        AppSettings settings,
        bool enableTun,
        ProxyRoutingMode routingMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedNode);
        ArgumentNullException.ThrowIfNull(ruleSets);
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(selectedNode.CoreId, "sing-box", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前内置节点生成器只能将可视化节点渲染为 sing-box 配置。请选择 sing-box 节点，或在设置中为 Mihomo 使用其原生配置文件。");
        }

        if (string.IsNullOrWhiteSpace(selectedNode.ConfigurationJson))
        {
            throw new InvalidDataException("该节点没有可运行的核心配置。请通过订阅更新导入，或在编辑节点时填写有效的 sing-box 出站 JSON。");
        }

        var outbound = ParseOutbound(selectedNode.ConfigurationJson);
        var nodeTag = $"node-{ToSafeSegment(selectedNode.Id)}";
        outbound["tag"] = nodeTag;

        var routeRules = routingMode == ProxyRoutingMode.Rules
            ? BuildRules(ruleSets, nodeTag)
            : new JsonArray();
        var defaultOutboundTag = routingMode == ProxyRoutingMode.Direct ? "direct" : nodeTag;
        if (enableTun)
        {
            // sing-box 1.13 removed the legacy per-inbound `sniff` fields.
            // Keep the same behavior through the supported route action.
            routeRules.Insert(0, new JsonObject { ["action"] = "sniff" });
            // Windows strict_route blocks port 53 outside the TUN interface.
            // Explicitly hand client DNS to sing-box, then send it through the
            // selected node.  A direct bootstrap resolver remains available
            // solely for resolving a domain used by that selected node.
            routeRules.Insert(1, new JsonObject
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns",
            });
        }

        var route = new JsonObject
        {
            ["rules"] = routeRules,
            ["final"] = defaultOutboundTag,
            ["auto_detect_interface"] = true,
        };
        var profile = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = true,
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "mixed",
                    ["tag"] = "mixed-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = ValidatePort(settings.MixedProxyPort),
                    // The Windows system proxy is managed explicitly by the
                    // user-selected mode, not silently by a core profile.
                    ["set_system_proxy"] = false,
                },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "block", ["tag"] = "block" },
                outbound,
            },
            ["route"] = route,
            // Both sing-box and Mihomo expose a Clash-compatible controller;
            // this loopback controller feeds the connections/traffic pages.
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = "127.0.0.1:9090",
                },
            },
        };

        if (enableTun)
        {
            profile["dns"] = BuildTunDns(routingMode == ProxyRoutingMode.Direct ? null : nodeTag);
            route["default_domain_resolver"] = "dns-bootstrap";
            SingBoxTunConfigBuilder.AddTunInbound(profile, settings.TunInterfaceName);
        }

        _paths.EnsureDirectories();
        var outputPath = Path.Combine(_paths.ProfilesDirectory, $"runtime-{ToSafeSegment(selectedNode.Id)}.json");
        var content = profile.ToJsonString(JsonStore.IndentedOptions);
        await WriteAtomicallyAsync(outputPath, content, cancellationToken);
        return new GeneratedProfile(outputPath, "sing-box", nodeTag, enableTun);
    }

    private static JsonObject BuildTunDns(string? selectedNodeTag)
    {
        var remote = new JsonObject
        {
            ["type"] = "udp",
            ["tag"] = "dns-remote",
            ["server"] = "1.1.1.1",
            ["server_port"] = 53,
        };
        if (!string.IsNullOrWhiteSpace(selectedNodeTag))
        {
            remote["detour"] = selectedNodeTag;
        }

        return new JsonObject
        {
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "udp",
                    ["tag"] = "dns-bootstrap",
                    ["server"] = "1.1.1.1",
                    ["server_port"] = 53,
                },
                remote,
            },
            ["final"] = "dns-remote",
        };
    }

    private static JsonObject ParseOutbound(string source)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(source);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("节点的 sing-box 出站 JSON 无法解析。", exception);
        }

        if (node is not JsonObject outbound ||
            outbound["type"]?.GetValue<string>() is not { Length: > 0 } type ||
            !SupportedSingBoxOutboundTypes.Contains(type))
        {
            throw new InvalidDataException("节点 JSON 必须是受支持的 sing-box 出站对象。");
        }

        // Serialize/parse gives the generated file its own mutable tree and
        // avoids retaining a reference to a user-editing JSON node.
        return JsonNode.Parse(outbound.ToJsonString())!.AsObject();
    }

    private static JsonArray BuildRules(IEnumerable<RuleSet> ruleSets, string proxyTag)
    {
        var rules = new JsonArray();
        foreach (var ruleSet in ruleSets.Where(static set => set.IsEnabled))
        {
            foreach (var entry in ruleSet.Entries
                .Where(static item => item.IsEnabled)
                .OrderBy(static item => item.Priority))
            {
                var rule = BuildRule(entry, proxyTag);
                if (rule is not null)
                {
                    rules.Add(rule);
                }
            }

            if (!string.IsNullOrWhiteSpace(ruleSet.ConfigurationJson))
            {
                var raw = ParseRawRule(ruleSet.ConfigurationJson);
                if (raw is not null)
                {
                    rules.Add(raw);
                }
            }
        }

        return rules;
    }

    private static JsonObject? BuildRule(RuleEntry entry, string proxyTag)
    {
        if (!string.IsNullOrWhiteSpace(entry.ConfigurationJson))
        {
            return ParseRawRule(entry.ConfigurationJson);
        }

        var values = entry.MatchValue
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(256)
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        var kind = entry.MatchKind.Trim().ToLowerInvariant();
        var rule = new JsonObject();
        switch (kind)
        {
            case "domain":
            case "domain_suffix":
            case "domain_keyword":
            case "ip_cidr":
            case "protocol":
            case "network":
            case "process_path":
            case "process_name":
            case "inbound":
                rule[kind] = ToArray(values);
                break;
            case "port":
                rule["port"] = ToPortArray(values);
                break;
            default:
                throw new InvalidDataException($"不支持的图形规则类型：{entry.MatchKind}。");
        }

        var outbound = entry.Action.Trim().ToLowerInvariant() switch
        {
            "direct" => "direct",
            "block" => "block",
            // The generated profile contains only direct, block and the
            // selected node. A graphical rule must therefore route "proxy"
            // to that selected node; custom graph/tag topologies belong in a
            // complete raw sing-box rule instead of referring to a tag that
            // does not exist in this generated profile.
            "proxy" => proxyTag,
            _ => throw new InvalidDataException($"不支持的规则动作：{entry.Action}。"),
        };
        rule["action"] = "route";
        rule["outbound"] = outbound;
        return rule;
    }

    private static JsonObject? ParseRawRule(string source)
    {
        try
        {
            return JsonNode.Parse(source) is JsonObject objectNode
                ? JsonNode.Parse(objectNode.ToJsonString())!.AsObject()
                : throw new InvalidDataException("规则 JSON 必须是对象。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("规则 JSON 无法解析。", exception);
        }
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray ToPortArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                throw new InvalidDataException($"规则端口“{value}”无效；请输入 1 到 65535 的整数。");
            }

            array.Add(port);
        }

        return array;
    }

    private static int ValidatePort(int port) => port is >= 1 and <= 65535
        ? port
        : throw new InvalidDataException("本地混合代理端口必须为 1–65535。");

    private static string ToSafeSegment(string value)
    {
        var normalized = new string(value.Where(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? Guid.NewGuid().ToString("N") : normalized;
    }

    private static async Task WriteAtomicallyAsync(string targetPath, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("无法确定配置输出目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed record GeneratedProfile(string Path, string CoreId, string SelectedOutboundTag, bool TunEnabled);
