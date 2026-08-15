using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WFly.Services;

/// <summary>
/// Reads the small, data-only subset of a Clash subscription that WFly can
/// safely convert into an individual sing-box outbound. This is deliberately
/// not a general YAML parser: tags, anchors, aliases, flow mappings and every
/// section other than the root <c>proxies</c> list are rejected or ignored.
/// </summary>
internal static class ClashYamlSubscriptionParser
{
    private const int MaximumNodes = 2_000;
    private const int MaximumNodeNameLength = 256;
    private const int MaximumScalarLength = 4_096;

    public static ClashYamlSubscriptionParseResult TryParse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var proxiesLine = FindRootProxiesLine(lines);
        if (proxiesLine < 0)
        {
            return ClashYamlSubscriptionParseResult.NotDetected;
        }

        var nodes = new List<ClashYamlSubscriptionNode>();
        var skippedCount = 0;
        Dictionary<string, string>? current = null;
        var itemIndent = -1;
        string? section = null;
        string? subSection = null;

        void CompleteCurrent()
        {
            if (current is null)
            {
                return;
            }

            try
            {
                var node = TryCreateVlessNode(current, nodes.Count + skippedCount + 1);
                if (node is null)
                {
                    skippedCount++;
                }
                else
                {
                    nodes.Add(node);
                }
            }
            catch (InvalidDataException)
            {
                // A malformed individual proxy must not block the rest of a
                // subscription. Never include its potentially secret fields in
                // an error or log.
                skippedCount++;
            }
            finally
            {
                current = null;
                section = null;
                subSection = null;
            }
        }

        for (var lineIndex = proxiesLine + 1; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.IndexOf('\t') >= 0)
            {
                throw new InvalidDataException("Clash YAML 订阅不能包含制表符缩进。");
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = CountLeadingSpaces(line);
            var payload = line[indent..];
            if (indent == 0 && !payload.StartsWith("- ", StringComparison.Ordinal))
            {
                CompleteCurrent();
                break;
            }

            if (payload.StartsWith("- ", StringComparison.Ordinal) && (itemIndent < 0 || indent == itemIndent))
            {
                CompleteCurrent();
                if (nodes.Count + skippedCount >= MaximumNodes)
                {
                    throw new InvalidDataException($"Clash YAML 中的代理条目超过 {MaximumNodes} 个限制。");
                }

                itemIndent = indent;
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!TryReadKeyValue(payload[2..], out var key, out var value) || value is null)
                {
                    throw new InvalidDataException("Clash YAML 的代理条目必须是普通键值映射。");
                }

                current[key] = value;
                continue;
            }

            if (current is null || itemIndent < 0)
            {
                throw new InvalidDataException("Clash YAML 的 proxies 段格式无效。");
            }

            if (!TryReadKeyValue(payload, out var property, out var propertyValue))
            {
                throw new InvalidDataException("Clash YAML 的代理字段格式无效。");
            }

            if (indent == itemIndent + 2)
            {
                section = propertyValue is null ? property : null;
                subSection = null;
                if (propertyValue is not null)
                {
                    current[property] = propertyValue;
                }

                continue;
            }

            if (section is not null && indent == itemIndent + 4)
            {
                subSection = propertyValue is null ? property : null;
                if (propertyValue is not null)
                {
                    current[$"{section}.{property}"] = propertyValue;
                }

                continue;
            }

            if (section is not null && subSection is not null && indent == itemIndent + 6 && propertyValue is not null)
            {
                current[$"{section}.{subSection}.{property}"] = propertyValue;
                continue;
            }

            throw new InvalidDataException("Clash YAML 仅支持常见的缩进式代理字段。");
        }

        CompleteCurrent();
        return new ClashYamlSubscriptionParseResult(true, nodes, skippedCount);
    }

    private static int FindRootProxiesLine(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (TryReadKeyValue(line, out var key, out _) && string.Equals(key, "proxies", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryReadKeyValue(string source, out string key, out string? value)
    {
        key = string.Empty;
        value = null;
        var colonIndex = source.IndexOf(':');
        if (colonIndex <= 0)
        {
            return false;
        }

        key = source[..colonIndex].Trim();
        if (!IsSafeKey(key))
        {
            return false;
        }

        var rawValue = StripInlineComment(source[(colonIndex + 1)..]).Trim();
        if (rawValue.Length == 0)
        {
            return true;
        }

        value = ReadScalar(rawValue);
        return true;
    }

    private static string ReadScalar(string rawValue)
    {
        if (rawValue.Length > MaximumScalarLength ||
            rawValue.StartsWith('!') || rawValue.StartsWith('&') || rawValue.StartsWith('*') ||
            rawValue.StartsWith('{') || rawValue.StartsWith('['))
        {
            throw new InvalidDataException("Clash YAML 包含不受支持的 YAML 构造。");
        }

        if (rawValue[0] == '"')
        {
            try
            {
                var value = JsonSerializer.Deserialize<string>(rawValue);
                return RequireSafeScalar(value, "YAML 字符串");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Clash YAML 的双引号字符串无效。", exception);
            }
        }

        if (rawValue[0] == '\'')
        {
            if (rawValue.Length < 2 || rawValue[^1] != '\'')
            {
                throw new InvalidDataException("Clash YAML 的单引号字符串无效。");
            }

            return RequireSafeScalar(rawValue[1..^1].Replace("''", "'", StringComparison.Ordinal), "YAML 字符串");
        }

        return RequireSafeScalar(rawValue, "YAML 字符串");
    }

    private static string StripInlineComment(string source)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaped = false;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (inDoubleQuote && character == '\\' && !escaped)
            {
                escaped = true;
                continue;
            }

            if (character == '"' && !inSingleQuote && !escaped)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '#' && !inSingleQuote && !inDoubleQuote && (index == 0 || char.IsWhiteSpace(source[index - 1])))
            {
                return source[..index];
            }

            escaped = false;
        }

        return source;
    }

    private static ClashYamlSubscriptionNode? TryCreateVlessNode(
        IReadOnlyDictionary<string, string> values,
        int ordinal)
    {
        if (!TryGet(values, "type", out var type) || !string.Equals(type, "vless", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tag = $"clash-vless-{ordinal:D4}";
        var server = RequireServer(GetRequired(values, "server", "服务器地址"));
        var port = ParsePort(GetRequired(values, "port", "服务器端口"));
        var uuid = RequireSafeScalar(GetRequired(values, "uuid", "UUID"), "UUID");
        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = tag,
            ["server"] = server,
            ["server_port"] = port,
            ["uuid"] = uuid,
        };

        if (TryGet(values, "flow", out var flow) && !string.IsNullOrWhiteSpace(flow))
        {
            if (!string.Equals(flow, "xtls-rprx-vision", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Clash YAML 中的 VLESS flow 不受支持。");
            }

            outbound["flow"] = flow;
        }

        ApplyTls(values, server, outbound);
        ApplyTransport(values, outbound);

        var name = TryGet(values, "name", out var configuredName) && !string.IsNullOrWhiteSpace(configuredName)
            ? RequireNodeName(configuredName)
            : $"Clash VLESS {ordinal}";
        var identityOutbound = JsonNode.Parse(outbound.ToJsonString())!.AsObject();
        identityOutbound.Remove("tag");
        var identityMaterial = identityOutbound.ToJsonString();
        var identity = $"clash-yaml:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityMaterial))).ToLowerInvariant()}";
        return new ClashYamlSubscriptionNode(outbound, name, "vless", identity);
    }

    private static void ApplyTls(IReadOnlyDictionary<string, string> values, string server, JsonObject outbound)
    {
        var hasReality = TryGet(values, "reality-opts.public-key", out var publicKey) && !string.IsNullOrWhiteSpace(publicKey);
        var tlsEnabled = hasReality || (TryGet(values, "tls", out var tlsValue) && ParseBoolean(tlsValue, "tls"));
        if (!tlsEnabled)
        {
            return;
        }

        var tls = new JsonObject { ["enabled"] = true };
        var serverName = TryGet(values, "servername", out var configuredServerName)
            ? configuredServerName
            : TryGet(values, "sni", out var configuredSni) ? configuredSni : server;
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            tls["server_name"] = RequireServer(serverName);
        }

        if (TryGet(values, "skip-cert-verify", out var insecure) && ParseBoolean(insecure, "skip-cert-verify"))
        {
            tls["insecure"] = true;
        }

        if (TryGet(values, "client-fingerprint", out var fingerprint) && !string.IsNullOrWhiteSpace(fingerprint))
        {
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = RequireSafeScalar(fingerprint, "TLS 指纹"),
            };
        }

        if (hasReality)
        {
            var reality = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = RequireSafeScalar(publicKey!, "Reality 公钥"),
            };
            if (TryGet(values, "reality-opts.short-id", out var shortId) && !string.IsNullOrWhiteSpace(shortId))
            {
                reality["short_id"] = RequireSafeScalar(shortId, "Reality short ID");
            }

            tls["reality"] = reality;
        }

        outbound["tls"] = tls;
    }

    private static void ApplyTransport(IReadOnlyDictionary<string, string> values, JsonObject outbound)
    {
        if (!TryGet(values, "network", out var configuredNetwork) || string.IsNullOrWhiteSpace(configuredNetwork))
        {
            return;
        }

        switch (configuredNetwork.Trim().ToLowerInvariant())
        {
            case "tcp":
            case "raw":
            case "none":
                return;
            case "ws":
            case "websocket":
            {
                var transport = new JsonObject { ["type"] = "ws" };
                if (TryGet(values, "ws-opts.path", out var path) && !string.IsNullOrWhiteSpace(path))
                {
                    transport["path"] = RequireSafeScalar(path, "WebSocket path");
                }

                var host = TryGet(values, "ws-opts.headers.host", out var headerHost)
                    ? headerHost
                    : TryGet(values, "ws-opts.host", out var optionHost) ? optionHost : null;
                if (!string.IsNullOrWhiteSpace(host))
                {
                    transport["headers"] = new JsonObject { ["Host"] = RequireServer(host) };
                }

                outbound["transport"] = transport;
                return;
            }
            case "grpc":
            {
                var transport = new JsonObject { ["type"] = "grpc" };
                var serviceName = TryGet(values, "grpc-opts.grpc-service-name", out var grpcService)
                    ? grpcService
                    : TryGet(values, "grpc-opts.service-name", out var serviceNameValue) ? serviceNameValue : null;
                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    transport["service_name"] = RequireSafeScalar(serviceName, "gRPC service name");
                }

                outbound["transport"] = transport;
                return;
            }
            default:
                throw new InvalidDataException("Clash YAML 中的 VLESS 传输类型不受支持。");
        }
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> values, string key, out string value) =>
        values.TryGetValue(key, out value!);

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string key, string name) =>
        TryGet(values, key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Clash YAML 的 VLESS 节点缺少{name}。");

    private static string RequireServer(string value)
    {
        var candidate = RequireSafeScalar(value, "服务器地址");
        if (candidate.Any(char.IsWhiteSpace) ||
            candidate.Contains('/') ||
            candidate.Contains('\\') ||
            Uri.CheckHostName(candidate) == UriHostNameType.Unknown)
        {
            throw new InvalidDataException("Clash YAML 的服务器地址无效。");
        }

        return candidate;
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65_535)
        {
            throw new InvalidDataException("Clash YAML 的服务器端口无效。");
        }

        return port;
    }

    private static bool ParseBoolean(string value, string name)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw new InvalidDataException($"Clash YAML 的 {name} 布尔值无效。"),
        };
    }

    private static string RequireNodeName(string value)
    {
        var name = RequireSafeScalar(value, "节点名称");
        if (name.Length > MaximumNodeNameLength)
        {
            throw new InvalidDataException("Clash YAML 的节点名称过长。");
        }

        return name;
    }

    private static string RequireSafeScalar(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumScalarLength || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"Clash YAML 的{name}无效。");
        }

        return value.Trim();
    }

    private static bool IsSafeKey(string key) =>
        key.Length is > 0 and <= 96 && key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static int CountLeadingSpaces(string line)
    {
        var index = 0;
        while (index < line.Length && line[index] == ' ')
        {
            index++;
        }

        return index;
    }
}

internal sealed record ClashYamlSubscriptionParseResult(
    bool IsDetected,
    IReadOnlyList<ClashYamlSubscriptionNode> Nodes,
    int SkippedCount)
{
    public static ClashYamlSubscriptionParseResult NotDetected { get; } = new(false, [], 0);
}

internal sealed record ClashYamlSubscriptionNode(JsonObject Outbound, string Name, string Protocol, string Identity);
