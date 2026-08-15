using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace WFly.Services;

/// <summary>
/// Performs only user-triggered egress, IP network-type, and Google reachability checks.
/// The IP-type lookup receives only the already discovered public egress address; no
/// subscription, node, configuration, or local-network information is sent.
/// </summary>
internal sealed class NetworkDiagnosticsService
{
    private static readonly Uri IpLookupUri = new("https://api.ipify.org?format=json");
    private static readonly Uri GoogleProbeUri = new("https://www.google.com/generate_204");

    public async Task<EgressCheckResult> CheckAsync(
        bool useLocalProxy,
        int localProxyPort,
        CancellationToken cancellationToken = default)
    {
        ValidatePort(localProxyPort);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = CreateClient(useLocalProxy, localProxyPort);

        try
        {
            var ipTask = GetPublicIpAsync(client, timeout.Token);
            var latencyTask = MeasureGoogleAsync(client, timeout.Token);
            await Task.WhenAll(ipTask, latencyTask);

            var ipResult = await ipTask;
            var latencyResult = await latencyTask;
            var typeResult = ipResult.Ip is { } ip
                ? await GetIpTypeAsync(client, ip, timeout.Token)
                : IpTypeLookupResult.Failure("未获取到出口 IP，无法进行类型检测");

            return new EgressCheckResult(
                ipResult.Ip,
                typeResult.NativeIpType,
                typeResult.ResidentialIpType,
                latencyResult.Latency,
                JoinErrors(ipResult.Error, typeResult.Error, latencyResult.Error),
                useLocalProxy)
            {
                IpTypeError = typeResult.DisplayError,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new EgressCheckResult(
                null,
                string.Empty,
                string.Empty,
                null,
                "出口检测超时，请检查网络后重试。",
                useLocalProxy)
            {
                IpTypeError = "检测超时，请重试",
            };
        }
    }

    private static async Task<(string? Ip, string? Error)> GetPublicIpAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(IpLookupUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var ip = document.RootElement.TryGetProperty("ip", out var element) ? element.GetString() : null;
            return IPAddress.TryParse(ip, out _) ? (ip, null) : (null, "出口 IP 服务返回了无效地址。");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return (null, $"出口 IP 检测失败：{exception.Message}");
        }
    }

    /// <summary>
    /// Looks up only the public egress IP through ProxyCheck's HTTPS v3 endpoint.
    /// tag=0 asks the provider not to persist this query in its positive-detection log.
    /// </summary>
    private static async Task<IpTypeLookupResult> GetIpTypeAsync(
        HttpClient client,
        string ip,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CreateIpTypeLookupUri(ip));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-store");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return IpTypeLookupResult.Failure($"分类服务返回 HTTP {(int)response.StatusCode}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var status = GetString(root, "status");
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
            {
                var message = GetString(root, "message");
                return IpTypeLookupResult.Failure(string.IsNullOrWhiteSpace(message)
                    ? "分类服务未返回有效结果"
                    : $"分类服务：{message}");
            }

            // V3 returns the lookup record under its IP-address key. Keeping the
            // fallback also makes the parser tolerant of a future flattened result.
            var record = root.TryGetProperty(ip, out var keyedRecord) && keyedRecord.ValueKind == JsonValueKind.Object
                ? keyedRecord
                : root;
            var networkType = record.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Object
                ? GetString(network, "type")
                : null;
            if (string.IsNullOrWhiteSpace(networkType))
            {
                return IpTypeLookupResult.Failure("分类服务未返回网络归属类型");
            }

            var nativeIpType = TranslateNetworkType(networkType);
            var residentialIpType = string.Equals(networkType, "Residential", StringComparison.OrdinalIgnoreCase)
                ? "是"
                : "否";
            return new IpTypeLookupResult(nativeIpType, residentialIpType, null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return IpTypeLookupResult.Failure($"分类服务连接失败：{exception.Message}");
        }
    }

    private static Uri CreateIpTypeLookupUri(string ip)
    {
        var escapedIp = Uri.EscapeDataString(ip);
        return new Uri($"https://proxycheck.io/v3/{escapedIp}?tag=0", UriKind.Absolute);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string TranslateNetworkType(string networkType) => networkType.Trim().ToLowerInvariant() switch
    {
        "residential" => "住宅",
        "business" => "企业",
        "wireless" => "移动网络",
        "hosting" => "数据中心",
        "tor" => "Tor 出口",
        "vpn" => "VPN",
        _ => networkType,
    };

    private static async Task<(TimeSpan? Latency, string? Error)> MeasureGoogleAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GoogleProbeUri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();
            return ((int)response.StatusCode is >= 200 and < 500
                ? stopwatch.Elapsed
                : null, (int)response.StatusCode is >= 200 and < 500
                    ? null
                    : $"Google 返回 HTTP {(int)response.StatusCode}。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return (null, $"Google 延迟检测失败：{exception.Message}");
        }
    }

    internal static HttpClient CreateClient(bool useLocalProxy, int localProxyPort)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = useLocalProxy,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        if (useLocalProxy)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{localProxyPort}", true);
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    private static string? JoinErrors(params string?[] errors)
    {
        var joined = string.Join(" ", errors.Where(static error => !string.IsNullOrWhiteSpace(error)));
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private sealed record IpTypeLookupResult(
        string NativeIpType,
        string ResidentialIpType,
        string? Error,
        string? DisplayError)
    {
        public static IpTypeLookupResult Failure(string reason) => new(
            string.Empty,
            string.Empty,
            $"IP 类型检测失败：{reason}。",
            reason);
    }
}

internal sealed record EgressCheckResult(
    string? IpAddress,
    string NativeIpType,
    string ResidentialIpType,
    TimeSpan? GoogleLatency,
    string? Error,
    bool UsedLocalProxy)
{
    /// <summary>
    /// A concise UI-facing explanation. It deliberately avoids a generic dash or
    /// “unknown”, so a user can distinguish a real classification from a retryable failure.
    /// </summary>
    public string? IpTypeError { get; init; }

    public string IpTypeDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(IpTypeError))
            {
                return $"检测失败：{IpTypeError}（可重试）";
            }

            if (string.IsNullOrWhiteSpace(NativeIpType) || string.IsNullOrWhiteSpace(ResidentialIpType))
            {
                return "检测失败：分类结果不完整（可重试）";
            }

            return $"原生 IP: {NativeIpType} · 住宅 IP: {ResidentialIpType}";
        }
    }
}
