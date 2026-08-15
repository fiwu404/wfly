using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace WFly.Services;

/// <summary>
/// Performs only user-triggered egress and Google reachability checks. IP
/// residential classification is intentionally reported as unknown because an
/// IP address alone cannot establish that property reliably.
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

        var ipTask = GetPublicIpAsync(client, timeout.Token);
        var latencyTask = MeasureGoogleAsync(client, timeout.Token);
        await Task.WhenAll(ipTask, latencyTask);

        var ipResult = await ipTask;
        var latencyResult = await latencyTask;
        return new EgressCheckResult(
            ipResult.Ip,
            // A reputable IP intelligence database is required to tell whether
            // an address is residential. Never infer it from an address range.
            "未知",
            "未知",
            latencyResult.Latency,
            JoinErrors(ipResult.Error, latencyResult.Error),
            useLocalProxy);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return (null, $"出口 IP 检测失败：{exception.Message}");
        }
    }

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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

    private static string? JoinErrors(string? left, string? right) =>
        string.Join(" ", new[] { left, right }.Where(static error => !string.IsNullOrWhiteSpace(error)));
}

internal sealed record EgressCheckResult(
    string? IpAddress,
    string NativeIpType,
    string ResidentialIpType,
    TimeSpan? GoogleLatency,
    string? Error,
    bool UsedLocalProxy)
{
    public string IpTypeDisplay => $"原生 IP: {NativeIpType} · 住宅 IP: {ResidentialIpType}";
}
