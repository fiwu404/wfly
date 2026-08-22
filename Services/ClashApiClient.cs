using System.Net;
using System.Text.Json;

namespace WFly.Services;

/// <summary>
/// Reads the loopback-only Clash-compatible controller exposed by sing-box or
/// Mihomo. The client never sends controller mutations and returns an empty
/// result when the user has not enabled the API in a running profile.
/// </summary>
internal sealed class ClashApiClient
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    public async Task<ClashConnectionsSnapshot?> TryGetConnectionsAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var client = CreateLoopbackClient();
        using var response = await client
            .GetAsync($"http://127.0.0.1:{port}/connections", HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await ParseLimitedAsync(stream, timeout.Token).ConfigureAwait(false);
        var root = document.RootElement;
        var uploadTotal = ReadInt64(root, "uploadTotal");
        var downloadTotal = ReadInt64(root, "downloadTotal");
        var connections = new List<ClashConnectionInfo>();
        if (root.TryGetProperty("connections", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Take(5_000))
            {
                connections.Add(ParseConnection(item));
            }
        }

        return new ClashConnectionsSnapshot(uploadTotal, downloadTotal, connections);
    }

    private static ClashConnectionInfo ParseConnection(JsonElement item)
    {
        var metadata = item.TryGetProperty("metadata", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
        var host = ReadString(metadata, "host") ?? ReadString(metadata, "destinationIP") ?? "未知";
        var destinationPort = ReadString(metadata, "destinationPort") ?? string.Empty;
        var chains = item.TryGetProperty("chains", out var chainArray) && chainArray.ValueKind == JsonValueKind.Array
            ? string.Join(" → ", chainArray.EnumerateArray().Select(static chain => chain.GetString()).Where(static chain => !string.IsNullOrWhiteSpace(chain)))
            : string.Empty;
        var usesDirectOutbound = item.TryGetProperty("chains", out var directChainArray) &&
            directChainArray.ValueKind == JsonValueKind.Array &&
            directChainArray.EnumerateArray()
                .Select(static chain => chain.GetString())
                .Any(static tag => string.Equals(tag, "direct", StringComparison.OrdinalIgnoreCase));
        return new ClashConnectionInfo(
            ReadString(item, "id") ?? string.Empty,
            host,
            destinationPort,
            ReadString(metadata, "network") ?? string.Empty,
            ReadString(metadata, "type") ?? string.Empty,
            ReadString(item, "rule") ?? string.Empty,
            chains,
            ReadInt64(item, "upload"),
            ReadInt64(item, "download"),
            ReadString(item, "start") ?? string.Empty,
            usesDirectOutbound);
    }

    private static async Task<JsonDocument> ParseLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var bounded = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (bounded.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("Clash API 响应过大。");
            }

            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        bounded.Position = 0;
        return await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateLoopbackClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var number) => Math.Max(0, number),
            JsonValueKind.String when long.TryParse(property.GetString(), out var parsed) => Math.Max(0, parsed),
            _ => 0,
        };
    }
}

internal sealed record ClashConnectionsSnapshot(long UploadTotal, long DownloadTotal, IReadOnlyList<ClashConnectionInfo> Connections);

internal sealed record ClashConnectionInfo(
    string Id,
    string Host,
    string Port,
    string Network,
    string Type,
    string Rule,
    string Chains,
    long UploadBytes,
    long DownloadBytes,
    string StartedAt,
    bool UsesDirectOutbound);
