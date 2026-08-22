using System.Diagnostics;

namespace WFly.Services;

internal sealed class SiteLatencyTestService
{
    public static IReadOnlyList<SiteLatencyTarget> DefaultTargets { get; } =
    [
        new("百度", new Uri("https://www.baidu.com/")),
        new("Google", new Uri("https://www.google.com/generate_204")),
        new("Netflix", new Uri("https://www.netflix.com/")),
        new("YouTube", new Uri("https://www.youtube.com/generate_204")),
        new("Disney+", new Uri("https://www.disneyplus.com/")),
        new("GitHub", new Uri("https://github.com/")),
        new("Pornhub", new Uri("https://www.pornhub.com/")),
    ];

    public async Task<IReadOnlyList<SiteLatencyResult>> TestAsync(
        IEnumerable<SiteLatencyTarget>? targets,
        bool useLocalProxy,
        int localProxyPort,
        IProgress<SiteLatencyResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selected = (targets ?? DefaultTargets).ToArray();
        using var gate = new SemaphoreSlim(3, 3);
        var tests = selected.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var result = await TestOneAsync(target, useLocalProxy, localProxyPort, cancellationToken);
                progress?.Report(result);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        return await Task.WhenAll(tests);
    }

    private static async Task<SiteLatencyResult> TestOneAsync(
        SiteLatencyTarget target,
        bool useLocalProxy,
        int localProxyPort,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = NetworkDiagnosticsService.CreateClient(useLocalProxy, localProxyPort);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WFly-Latency-Test/1.0");
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            stopwatch.Stop();
            return new SiteLatencyResult(target.Name, target.Uri.Host, stopwatch.Elapsed, (int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new SiteLatencyResult(target.Name, target.Uri.Host, null, null, "请求超时");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new SiteLatencyResult(target.Name, target.Uri.Host, null, null, exception.Message);
        }
    }
}

internal sealed record SiteLatencyTarget(string Name, Uri Uri);

internal sealed record SiteLatencyResult(
    string Name,
    string Host,
    TimeSpan? Latency,
    int? StatusCode,
    string? Error)
{
    public string StatusText => Error is not null
        ? "失败"
        : StatusCode is { } statusCode
            ? $"HTTP {statusCode}"
            : "未知";

    /// <summary>
    /// HTTP-level reachability only. A successful page response must not be
    /// presented as proof that a logged-in streaming account can play content
    /// in a particular region.
    /// </summary>
    public string UnlockStatusText => Error is not null
        ? "不可访问"
        : StatusCode is >= 200 and < 400
            ? "网页可访问"
            : StatusCode is 401 or 403 or 451
                ? "访问受限"
                : StatusCode is { } statusCode
                    ? $"响应异常（HTTP {statusCode}）"
                    : "未知";
}
