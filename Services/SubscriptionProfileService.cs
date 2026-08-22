using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Converts a user-provided HTTPS subscription into a local sing-box profile
/// or durable nodes. The service never logs a subscription URL; group storage
/// owns the URL when scheduled refresh is enabled.
/// </summary>
internal sealed class SubscriptionProfileService
{
    private const int MaximumSubscriptionBytes = 8 * 1024 * 1024;
    private const int MaximumRedirects = 3;
    private const int MaximumNodes = 2_000;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;

    public SubscriptionProfileService(HttpClient httpClient, AppPaths paths)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<SubscriptionImportResult> ImportAsync(
        string subscriptionUrl,
        CancellationToken cancellationToken = default)
    {
        var subscription = await DownloadAndParseAsync(subscriptionUrl, cancellationToken);

        var profile = BuildSingBoxProfile(subscription.Nodes);
        var serializedProfile = profile.ToJsonString(JsonStore.IndentedOptions);

        _paths.EnsureDirectories();
        var profilePath = Path.Combine(
            _paths.ProfilesDirectory,
            $"subscription-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        await WriteProfileAtomicallyAsync(profilePath, serializedProfile, cancellationToken);

        return new SubscriptionImportResult(
            profilePath,
            subscription.Nodes.Count,
            subscription.SkippedCount,
            subscription.SourceHost);
    }

    /// <summary>
    /// Converts one supported share link into a detached sing-box outbound for
    /// the manual-node editor. The caller owns persistence and must never put
    /// the source link into a log message because it can contain credentials.
    /// </summary>
    public ParsedShareLink ParseSingleShareLink(string shareLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareLink);
        try
        {
            var parsed = ParseNode(shareLink.Trim(), "manual-node", 1)
                ?? throw new SubscriptionFormatException("Unsupported node link scheme.");
            return new ParsedShareLink(
                parsed.Protocol,
                parsed.Name,
                parsed.Outbound.ToJsonString());
        }
        catch (SubscriptionFormatException exception)
        {
            throw new InvalidDataException("无法解析该分享链接。当前支持 SS、VMess、VLESS、Trojan。", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("分享链接包含无效的节点 JSON。", exception);
        }
    }

    /// <summary>
    /// Refreshes a subscription-backed group and atomically replaces only that
    /// group's durable nodes. Standard share links are rendered for sing-box;
    /// the subscription URL itself is never written to a log or node record.
    /// </summary>
    public async Task<SubscriptionGroupImportResult> RefreshGroupAsync(
        NodeGroup group,
        ProxyNodeStore proxyNodeStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(proxyNodeStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(group.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(group.SubscriptionUrl);

        var subscription = await DownloadAndParseAsync(group.SubscriptionUrl, cancellationToken);
        var existingNodes = await proxyNodeStore.GetByGroupAsync(group.Id, cancellationToken);
        var existingByShareLink = existingNodes
            .Where(static node => !string.IsNullOrWhiteSpace(node.ShareLink))
            .GroupBy(node => node.ShareLink!, StringComparer.Ordinal)
            .ToDictionary(
                static groupByLink => groupByLink.Key,
                static groupByLink => new Queue<ProxyNode>(groupByLink.OrderBy(static node => node.CreatedAt)),
                StringComparer.Ordinal);

        var replacements = new List<ProxyNode>(subscription.Nodes.Count);
        foreach (var parsedNode in subscription.Nodes)
        {
            ProxyNode? existing = null;
            if (existingByShareLink.TryGetValue(parsedNode.ShareLink, out var matchingNodes) &&
                matchingNodes.Count > 0)
            {
                existing = matchingNodes.Dequeue();
            }

            replacements.Add(new ProxyNode
            {
                Id = existing?.Id ?? string.Empty,
                GroupId = group.Id,
                Name = parsedNode.Name,
                Protocol = parsedNode.Protocol,
                // All supported URL schemes above are converted to sing-box
                // outbound JSON. A non-URL configuration must be identified as
                // Clash YAML before any future importer selects mihomo instead.
                CoreId = "sing-box",
                ShareLink = parsedNode.ShareLink,
                ConfigurationJson = parsedNode.Outbound.ToJsonString(),
                PingResult = existing?.PingResult,
                TcpingResult = existing?.TcpingResult,
                RealConnectionResult = existing?.RealConnectionResult,
                UdpResult = existing?.UdpResult,
                LastTestedAt = existing?.LastTestedAt,
                IsEnabled = existing?.IsEnabled ?? true,
                CreatedAt = existing?.CreatedAt ?? default,
            });
        }

        var storedNodes = await proxyNodeStore.ReplaceForGroupAsync(
            group.Id,
            replacements,
            cancellationToken);

        return new SubscriptionGroupImportResult(
            storedNodes,
            subscription.SkippedCount,
            subscription.SourceHost,
            "sing-box");
    }

    private async Task<ParsedSubscription> DownloadAndParseAsync(
        string subscriptionUrl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionUrl);

        var initialUri = ValidateSubscriptionUri(subscriptionUrl);
        (string content, Uri finalUri) downloaded;
        try
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(30));
            downloaded = await DownloadSubscriptionAsync(initialUri, timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("订阅下载超时，请稍后重试。");
        }
        catch (HttpRequestException)
        {
            // Do not expose a token-bearing URL through a caller's error log.
            throw new InvalidDataException("无法下载订阅内容。");
        }

        var sourceHost = string.IsNullOrWhiteSpace(downloaded.finalUri.IdnHost)
            ? downloaded.finalUri.Host
            : downloaded.finalUri.IdnHost;

        var clashSubscription = ClashYamlSubscriptionParser.TryParse(downloaded.content);
        if (!clashSubscription.IsDetected && TryDecodeBase64Text(downloaded.content.Trim(), out var decodedContent))
        {
            clashSubscription = ClashYamlSubscriptionParser.TryParse(decodedContent);
        }

        if (clashSubscription.IsDetected)
        {
            if (clashSubscription.Nodes.Count == 0)
            {
                throw new InvalidDataException("Clash YAML 订阅中没有可导入的 VLESS 节点。");
            }

            var clashNodes = clashSubscription.Nodes
                .Select((node, index) =>
                {
                    var tag = $"node-{index + 1:D4}";
                    node.Outbound["tag"] = tag;
                    return new ParsedNode(
                        node.Outbound,
                        tag,
                        node.Protocol,
                        node.Name,
                        node.Identity);
                })
                .ToArray();
            return new ParsedSubscription(clashNodes, clashSubscription.SkippedCount, sourceHost);
        }

        var links = ExtractLinks(downloaded.content);
        var nodes = new List<ParsedNode>();
        var skippedCount = 0;

        foreach (var link in links)
        {
            if (nodes.Count >= MaximumNodes)
            {
                throw new InvalidDataException($"订阅中的有效节点超过 {MaximumNodes} 个限制。");
            }

            try
            {
                var node = ParseNode(link, $"node-{nodes.Count + 1:D4}", nodes.Count + 1);
                if (node is null)
                {
                    skippedCount++;
                    continue;
                }

                nodes.Add(node);
            }
            catch (SubscriptionFormatException)
            {
                // Do not include the source link in an error or log: it can contain credentials.
                skippedCount++;
            }
            catch (JsonException)
            {
                skippedCount++;
            }
        }

        if (nodes.Count == 0)
        {
            throw new InvalidDataException("订阅中没有可导入的 SS、VMess、VLESS 或 Trojan 节点。");
        }

        return new ParsedSubscription(nodes, skippedCount, sourceHost);
    }

    private async Task<(string content, Uri finalUri)> DownloadSubscriptionAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var currentUri = initialUri;

        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*") { Quality = 0.1 });

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount >= MaximumRedirects || response.Headers.Location is null)
                {
                    throw new InvalidDataException("订阅重定向次数过多或响应缺少重定向地址。");
                }

                var nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                currentUri = ValidateSubscriptionUri(nextUri.AbsoluteUri);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException($"订阅服务器返回 HTTP {(int)response.StatusCode}。");
            }

            if (response.Content.Headers.ContentLength is { } contentLength && contentLength > MaximumSubscriptionBytes)
            {
                throw new InvalidDataException($"订阅内容超过 {MaximumSubscriptionBytes / 1024 / 1024} MB 限制。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await ReadLimitedBytesAsync(stream, cancellationToken);
            try
            {
                return (StrictUtf8.GetString(bytes), currentUri);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("订阅内容不是有效的 UTF-8 文本。", exception);
            }
        }
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        await using var destination = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumSubscriptionBytes)
            {
                throw new InvalidDataException($"订阅内容超过 {MaximumSubscriptionBytes / 1024 / 1024} MB 限制。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return destination.ToArray();
    }

    private static Uri ValidateSubscriptionUri(string rawUri)
    {
        if (!Uri.TryCreate(rawUri.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("订阅链接必须是没有用户名或密码的 HTTPS 地址。");
        }

        return uri;
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode) =>
        (int)statusCode is 301 or 302 or 303 or 307 or 308;

    private static IReadOnlyList<string> ExtractLinks(string content)
    {
        var candidate = content.Trim().TrimStart('\uFEFF');
        if (candidate.Length == 0)
        {
            throw new InvalidDataException("订阅内容为空。");
        }

        if (!ContainsSupportedScheme(candidate))
        {
            if (!TryDecodeBase64Text(candidate, out var decoded) || !ContainsSupportedScheme(decoded))
            {
                throw new InvalidDataException("订阅不是受支持的 Base64 或逐行节点链接格式。");
            }

            candidate = decoded;
        }

        var links = candidate
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => line.Trim('\r'))
            .Where(static line => line.Length > 0)
            .ToArray();

        if (links.Length == 0)
        {
            throw new InvalidDataException("订阅中没有节点链接。");
        }

        return links;
    }

    private static bool ContainsSupportedScheme(string text) =>
        text.Contains("ss://", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("vmess://", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("vless://", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("trojan://", StringComparison.OrdinalIgnoreCase);

    private static ParsedNode? ParseNode(string link, string tag, int ordinal)
    {
        if (link.Length > 32_768)
        {
            throw new SubscriptionFormatException("节点链接过长。");
        }

        if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
        {
            return CreateParsedNode(ParseShadowsocks(link, tag), tag, "ss", link, ordinal);
        }

        if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            return CreateParsedNode(ParseVmess(link, tag), tag, "vmess", link, ordinal);
        }

        if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            return CreateParsedNode(ParseVless(link, tag), tag, "vless", link, ordinal);
        }

        if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
        {
            return CreateParsedNode(ParseTrojan(link, tag), tag, "trojan", link, ordinal);
        }

        return null;
    }

    private static ParsedNode CreateParsedNode(
        JsonObject outbound,
        string tag,
        string protocol,
        string shareLink,
        int ordinal) =>
        new(
            outbound,
            tag,
            protocol,
            GetDisplayName(shareLink, protocol, ordinal),
            shareLink);

    private static string GetDisplayName(string shareLink, string protocol, int ordinal)
    {
        string? candidate = protocol switch
        {
            "vmess" => GetVmessDisplayName(shareLink),
            _ => GetUriFragmentDisplayName(shareLink),
        };

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var normalized = candidate.Trim();
            if (normalized.Length <= 256 && !normalized.Any(char.IsControl))
            {
                return normalized;
            }
        }

        return $"{protocol.ToUpperInvariant()} {ordinal}";
    }

    private static string? GetUriFragmentDisplayName(string shareLink)
    {
        var fragmentIndex = shareLink.IndexOf('#');
        if (fragmentIndex < 0 || fragmentIndex == shareLink.Length - 1)
        {
            return null;
        }

        try
        {
            return DecodeComponent(shareLink[(fragmentIndex + 1)..]);
        }
        catch (SubscriptionFormatException)
        {
            return null;
        }
    }

    private static string? GetVmessDisplayName(string shareLink)
    {
        var encoded = shareLink["vmess://".Length..];
        var fragmentIndex = encoded.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            encoded = encoded[..fragmentIndex];
        }

        if (!TryDecodeBase64String(encoded, out var json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? GetVmessValue(document.RootElement, "ps")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject ParseShadowsocks(string link, string tag)
    {
        var body = link["ss://".Length..];
        var fragmentIndex = body.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            body = body[..fragmentIndex];
        }

        var queryIndex = body.IndexOf('?');
        var query = queryIndex >= 0 ? ParseQuery(body[(queryIndex + 1)..]) : EmptyParameters;
        var principal = queryIndex >= 0 ? body[..queryIndex] : body;
        var separatorIndex = principal.LastIndexOf('@');

        string credentials;
        string endpoint;
        if (separatorIndex >= 0)
        {
            credentials = DecodeShadowsocksCredentials(principal[..separatorIndex]);
            endpoint = DecodeComponent(principal[(separatorIndex + 1)..]);
        }
        else
        {
            if (!TryDecodeBase64String(principal, out var decodedPrincipal))
            {
                throw new SubscriptionFormatException("无效的 Shadowsocks 链接。");
            }

            separatorIndex = decodedPrincipal.LastIndexOf('@');
            if (separatorIndex <= 0 || separatorIndex == decodedPrincipal.Length - 1)
            {
                throw new SubscriptionFormatException("无效的 Shadowsocks 链接。");
            }

            credentials = decodedPrincipal[..separatorIndex];
            endpoint = decodedPrincipal[(separatorIndex + 1)..];
        }

        var credentialSeparator = credentials.IndexOf(':');
        if (credentialSeparator <= 0 || credentialSeparator == credentials.Length - 1)
        {
            throw new SubscriptionFormatException("无效的 Shadowsocks 凭据。");
        }

        var method = RequireSafeText(credentials[..credentialSeparator], "加密方法", 128);
        var password = RequireSafeText(credentials[(credentialSeparator + 1)..], "密码", 4_096);
        var (server, port) = ParseServerAndPort(endpoint);

        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["tag"] = tag,
            ["server"] = server,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password,
        };

        var pluginSpecification = GetParameter(query, "plugin");
        if (!string.IsNullOrWhiteSpace(pluginSpecification))
        {
            ApplyShadowsocksPlugin(outbound, pluginSpecification);
        }

        return outbound;
    }

    private static JsonObject ParseVmess(string link, string tag)
    {
        var encoded = link["vmess://".Length..];
        var fragmentIndex = encoded.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            encoded = encoded[..fragmentIndex];
        }

        if (!TryDecodeBase64String(encoded, out var json))
        {
            throw new SubscriptionFormatException("无效的 VMess 链接。");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SubscriptionFormatException("无效的 VMess 节点对象。");
        }

        var server = RequireServer(GetVmessValue(root, "add") ?? GetVmessValue(root, "server"));
        var port = ParsePort(GetVmessValue(root, "port"));
        var uuid = RequireSafeText(GetVmessValue(root, "id"), "UUID", 256);
        var security = NormalizeVmessSecurity(GetVmessValue(root, "scy") ?? GetVmessValue(root, "security"));
        var alterId = ParseNonNegativeInt(GetVmessValue(root, "aid"), "alterId");

        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["tag"] = tag,
            ["server"] = server,
            ["server_port"] = port,
            ["uuid"] = uuid,
            ["security"] = security,
            ["alter_id"] = alterId,
        };

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyVmessParameter(root, parameters, "tls", "security");
        CopyVmessParameter(root, parameters, "sni", "sni");
        CopyVmessParameter(root, parameters, "host", "host");
        CopyVmessParameter(root, parameters, "path", "path");
        CopyVmessParameter(root, parameters, "net", "type");
        CopyVmessParameter(root, parameters, "type", "headerType");
        CopyVmessParameter(root, parameters, "alpn", "alpn");
        CopyVmessParameter(root, parameters, "fp", "fp");
        CopyVmessParameter(root, parameters, "allowInsecure", "allowInsecure");
        CopyVmessParameter(root, parameters, "pbk", "pbk");
        CopyVmessParameter(root, parameters, "sid", "sid");
        CopyVmessParameter(root, parameters, "serviceName", "serviceName");
        ApplyTlsAndTransport(outbound, parameters, server, requireTls: false);

        return outbound;
    }

    private static JsonObject ParseVless(string link, string tag)
    {
        var (uri, parameters) = ParseShareUri(link, "vless");
        var server = RequireServer(uri.Host);
        var uuid = RequireSafeText(DecodeComponent(uri.UserInfo), "UUID", 256);

        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = tag,
            ["server"] = server,
            ["server_port"] = ValidatePort(uri.Port),
            ["uuid"] = uuid,
        };

        var flow = GetParameter(parameters, "flow");
        if (!string.IsNullOrWhiteSpace(flow))
        {
            if (!string.Equals(flow, "xtls-rprx-vision", StringComparison.Ordinal))
            {
                throw new SubscriptionFormatException("不支持的 VLESS flow。");
            }

            outbound["flow"] = flow;
        }

        ApplyTlsAndTransport(outbound, parameters, server, requireTls: false);
        return outbound;
    }

    private static JsonObject ParseTrojan(string link, string tag)
    {
        var (uri, parameters) = ParseShareUri(link, "trojan");
        var server = RequireServer(uri.Host);
        var password = RequireSafeText(DecodeComponent(uri.UserInfo), "密码", 4_096);

        var outbound = new JsonObject
        {
            ["type"] = "trojan",
            ["tag"] = tag,
            ["server"] = server,
            ["server_port"] = ValidatePort(uri.Port),
            ["password"] = password,
        };

        ApplyTlsAndTransport(outbound, parameters, server, requireTls: true);
        return outbound;
    }

    private static (Uri uri, IReadOnlyDictionary<string, string> parameters) ParseShareUri(string link, string expectedScheme)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            string.IsNullOrWhiteSpace(uri.UserInfo) ||
            uri.Port <= 0)
        {
            throw new SubscriptionFormatException("无效的节点链接。");
        }

        return (uri, ParseQuery(uri.Query));
    }

    private static void ApplyTlsAndTransport(
        JsonObject outbound,
        IReadOnlyDictionary<string, string> parameters,
        string server,
        bool requireTls)
    {
        var security = (GetParameter(parameters, "security") ?? string.Empty).Trim();
        var securityName = security.ToLowerInvariant();
        var isReality = string.Equals(securityName, "reality", StringComparison.Ordinal);
        var usesTls = requireTls || isReality ||
            string.Equals(securityName, "tls", StringComparison.Ordinal) ||
            string.Equals(securityName, "xtls", StringComparison.Ordinal);

        if (!usesTls && securityName.Length > 0 &&
            !string.Equals(securityName, "none", StringComparison.Ordinal) &&
            !string.Equals(securityName, "0", StringComparison.Ordinal))
        {
            throw new SubscriptionFormatException("不支持的 TLS 类型。");
        }

        if (usesTls)
        {
            var tls = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = RequireSafeText(
                    GetParameter(parameters, "sni") ?? GetParameter(parameters, "serverName") ?? server,
                    "SNI",
                    512),
            };

            if (IsTrue(GetParameter(parameters, "allowInsecure")))
            {
                tls["insecure"] = true;
            }

            var alpn = SplitCommaSeparated(GetParameter(parameters, "alpn"), "ALPN", 32, 128);
            if (alpn.Count > 0)
            {
                tls["alpn"] = ToJsonArray(alpn);
            }

            var fingerprint = GetParameter(parameters, "fp");
            if (!string.IsNullOrWhiteSpace(fingerprint) &&
                !string.Equals(fingerprint, "none", StringComparison.OrdinalIgnoreCase))
            {
                tls["utls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["fingerprint"] = RequireSafeText(fingerprint, "TLS 指纹", 64),
                };
            }

            if (isReality)
            {
                var publicKey = RequireSafeText(
                    GetParameter(parameters, "pbk") ?? GetParameter(parameters, "publicKey"),
                    "Reality 公钥",
                    512);
                var reality = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = publicKey,
                };

                var shortId = GetParameter(parameters, "sid") ?? GetParameter(parameters, "shortId");
                if (!string.IsNullOrWhiteSpace(shortId))
                {
                    reality["short_id"] = RequireSafeText(shortId, "Reality short_id", 128);
                }

                tls["reality"] = reality;
            }

            outbound["tls"] = tls;
        }

        ApplyTransport(outbound, parameters);
    }

    private static void ApplyTransport(JsonObject outbound, IReadOnlyDictionary<string, string> parameters)
    {
        var transportType = (GetParameter(parameters, "type") ?? string.Empty).Trim().ToLowerInvariant();
        var headerType = (GetParameter(parameters, "headerType") ?? string.Empty).Trim();
        if (transportType.Length == 0 ||
            transportType is "tcp" or "raw" or "none")
        {
            if (headerType.Length > 0 && !string.Equals(headerType, "none", StringComparison.OrdinalIgnoreCase))
            {
                throw new SubscriptionFormatException("不支持的 TCP 伪装类型。");
            }

            return;
        }

        JsonObject transport;
        switch (transportType)
        {
            case "ws":
            case "websocket":
                transport = new JsonObject { ["type"] = "ws" };
                AddPath(transport, parameters);
                AddHostHeader(transport, parameters);
                break;

            case "grpc":
            case "gun":
                transport = new JsonObject
                {
                    ["type"] = "grpc",
                    ["service_name"] = RequireSafeText(
                        GetParameter(parameters, "serviceName") ?? GetParameter(parameters, "service_name"),
                        "gRPC service_name",
                        1_024),
                };
                break;

            case "http":
            case "h2":
                transport = new JsonObject { ["type"] = "http" };
                var hosts = SplitCommaSeparated(GetParameter(parameters, "host"), "HTTP host", 32, 512);
                if (hosts.Count > 0)
                {
                    transport["host"] = ToJsonArray(hosts);
                }

                AddPath(transport, parameters);
                break;

            case "httpupgrade":
                transport = new JsonObject { ["type"] = "httpupgrade" };
                var host = GetParameter(parameters, "host");
                if (!string.IsNullOrWhiteSpace(host))
                {
                    transport["host"] = RequireSafeText(host.Split(',', 2)[0], "HTTPUpgrade host", 512);
                }

                AddPath(transport, parameters);
                break;

            default:
                throw new SubscriptionFormatException("不支持的传输类型。");
        }

        outbound["transport"] = transport;
    }

    private static void AddPath(JsonObject transport, IReadOnlyDictionary<string, string> parameters)
    {
        var path = GetParameter(parameters, "path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            transport["path"] = RequireSafeText(path, "传输路径", 4_096);
        }
    }

    private static void AddHostHeader(JsonObject transport, IReadOnlyDictionary<string, string> parameters)
    {
        var host = GetParameter(parameters, "host");
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        transport["headers"] = new JsonObject
        {
            ["Host"] = RequireSafeText(host.Split(',', 2)[0], "WebSocket Host", 512),
        };
    }

    private static void ApplyShadowsocksPlugin(JsonObject outbound, string pluginSpecification)
    {
        var parts = pluginSpecification.Split(';', 2);
        var plugin = RequireSafeText(parts[0], "Shadowsocks 插件", 64);
        if (!string.Equals(plugin, "v2ray-plugin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(plugin, "obfs-local", StringComparison.OrdinalIgnoreCase))
        {
            throw new SubscriptionFormatException("不支持的 Shadowsocks 插件。");
        }

        outbound["plugin"] = plugin;
        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            outbound["plugin_opts"] = RequireSafeText(parts[1], "Shadowsocks 插件参数", 4_096);
        }
    }

    private static JsonObject BuildSingBoxProfile(IReadOnlyList<ParsedNode> nodes)
    {
        var outboundTags = new JsonArray();
        var outbounds = new JsonArray();
        foreach (var node in nodes)
        {
            outbounds.Add(node.Outbound);
            outboundTags.Add(JsonValue.Create(node.Tag));
        }

        outbounds.Add(new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = "proxy",
            ["outbounds"] = outboundTags,
            ["default"] = nodes[0].Tag,
            ["interrupt_exist_connections"] = false,
        });

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "mixed",
                    ["tag"] = "mixed-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = 2080,
                    ["set_system_proxy"] = false,
                },
            },
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["final"] = "proxy",
                ["auto_detect_interface"] = true,
            },
        };
    }

    private static async Task WriteProfileAtomicallyAsync(string profilePath, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(profilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("无法确定订阅配置目录。");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(profilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(temporaryPath, profilePath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; the profile itself was never overwritten.
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            var key = DecodeComponent(separator >= 0 ? component[..separator] : component);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = DecodeComponent(separator >= 0 ? component[(separator + 1)..] : string.Empty);
            parameters[key] = value;
        }

        return parameters;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string? GetParameter(IReadOnlyDictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var value) ? value : null;

    private static void CopyVmessParameter(
        JsonElement source,
        IDictionary<string, string> destination,
        string sourceName,
        string destinationName)
    {
        var value = GetVmessValue(source, sourceName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            destination[destinationName] = value;
        }
    }

    private static string? GetVmessValue(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }

        return null;
    }

    private static string DecodeShadowsocksCredentials(string encoded)
    {
        var decoded = DecodeComponent(encoded);
        if (decoded.Contains(':'))
        {
            return decoded;
        }

        if (TryDecodeBase64String(decoded, out var decodedCredentials) && decodedCredentials.Contains(':'))
        {
            return decodedCredentials;
        }

        throw new SubscriptionFormatException("无效的 Shadowsocks 凭据。");
    }

    private static bool TryDecodeBase64Text(string input, out string decoded) =>
        TryDecodeBase64String(input, out decoded);

    private static bool TryDecodeBase64String(string input, out string decoded)
    {
        decoded = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var compact = new StringBuilder(input.Length);
        foreach (var character in input)
        {
            if (!char.IsWhiteSpace(character))
            {
                compact.Append(character);
            }
        }

        var base64 = compact.ToString().Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
            case 1:
                return false;
        }

        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(base64)).TrimStart('\uFEFF');
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string DecodeComponent(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            throw new SubscriptionFormatException("节点链接中的转义字符无效。", exception);
        }
    }

    private static (string server, int port) ParseServerAndPort(string endpoint)
    {
        var value = endpoint.Trim();
        string server;
        string portText;
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closingIndex = value.IndexOf(']');
            if (closingIndex <= 1 || closingIndex + 1 >= value.Length || value[closingIndex + 1] != ':')
            {
                throw new SubscriptionFormatException("无效的服务器地址。");
            }

            server = value[1..closingIndex];
            portText = value[(closingIndex + 2)..];
        }
        else
        {
            var separatorIndex = value.LastIndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
            {
                throw new SubscriptionFormatException("无效的服务器地址。");
            }

            server = value[..separatorIndex];
            portText = value[(separatorIndex + 1)..];
        }

        return (RequireServer(server), ParsePort(portText));
    }

    private static string RequireServer(string? server)
    {
        var value = RequireSafeText(server, "服务器地址", 512);
        if (Uri.CheckHostName(value) == UriHostNameType.Unknown)
        {
            throw new SubscriptionFormatException("无效的服务器地址。");
        }

        return value;
    }

    private static int ParsePort(string? portText)
    {
        if (!int.TryParse(portText, out var port))
        {
            throw new SubscriptionFormatException("无效的服务器端口。");
        }

        return ValidatePort(port);
    }

    private static int ValidatePort(int port)
    {
        if (port is < 1 or > 65_535)
        {
            throw new SubscriptionFormatException("无效的服务器端口。");
        }

        return port;
    }

    private static int ParseNonNegativeInt(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (!int.TryParse(text, out var value) || value < 0)
        {
            throw new SubscriptionFormatException($"无效的 {name}。");
        }

        return value;
    }

    private static string NormalizeVmessSecurity(string? security)
    {
        var value = string.IsNullOrWhiteSpace(security) ? "auto" : security.Trim().ToLowerInvariant();
        return value is "auto" or "none" or "zero" or "aes-128-gcm" or "chacha20-poly1305" or "aes-128-ctr"
            ? value
            : throw new SubscriptionFormatException("不支持的 VMess 加密方法。");
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> SplitCommaSeparated(string? value, string fieldName, int maximumItems, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length > maximumItems)
        {
            throw new SubscriptionFormatException($"{fieldName} 数量过多。");
        }

        return values.Select(item => RequireSafeText(item, fieldName, maximumLength)).ToArray();
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    private static string RequireSafeText(string? value, string fieldName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new SubscriptionFormatException($"无效的 {fieldName}。");
        }

        return value;
    }

    private sealed record ParsedSubscription(
        IReadOnlyList<ParsedNode> Nodes,
        int SkippedCount,
        string SourceHost);

    private sealed record ParsedNode(
        JsonObject Outbound,
        string Tag,
        string Protocol,
        string Name,
        string ShareLink);

    private sealed class SubscriptionFormatException : Exception
    {
        public SubscriptionFormatException(string message)
            : base(message)
        {
        }

        public SubscriptionFormatException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

/// <summary>
/// The durable result of a subscription refresh. <see cref="Nodes"/> were
/// persisted as one atomic replacement for their node group.
/// </summary>
internal sealed record SubscriptionGroupImportResult(
    IReadOnlyList<ProxyNode> Nodes,
    int SkippedCount,
    string SourceHost,
    string DetectedCoreId);

/// <summary>Safe, detached output for a user-provided single share link.</summary>
internal sealed record ParsedShareLink(string Protocol, string Name, string ConfigurationJson);
