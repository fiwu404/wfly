using System.Net;
using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Downloads the small, fixed smart-routing GeoFiles into the portable data
/// directory. The files are local sing-box binary rule-sets (.srs), not the
/// removed legacy geoip/geosite fields.
/// </summary>
internal sealed class GeoFileService
{
    private const long MaximumFileLength = 32L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GeoFileService(HttpClient httpClient, AppPaths paths)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public bool HasSmartRoutingFiles() => GeoFileRegistry.SmartRouting.All(IsAvailable);

    public string GetLocalPath(GeoFileDefinition definition) =>
        Path.Combine(_paths.GeoFilesDirectory, definition.FileName);

    public async Task<GeoFileState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        return await JsonStore.ReadOrDefaultAsync(
            _paths.GeoFilesStateFile,
            static () => new GeoFileState(),
            cancellationToken);
    }

    public async Task<GeoFileState> UpdateSmartRoutingFilesAsync(
        IProgress<GeoFileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureDirectories();
            var state = await GetStateAsync(cancellationToken);
            for (var index = 0; index < GeoFileRegistry.SmartRouting.Count; index++)
            {
                var definition = GeoFileRegistry.SmartRouting[index];
                await DownloadAsync(definition, state, index + 1, GeoFileRegistry.SmartRouting.Count, progress, cancellationToken);
            }

            await JsonStore.WriteAtomicallyAsync(_paths.GeoFilesStateFile, state, cancellationToken);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAsync(
        GeoFileDefinition definition,
        GeoFileState state,
        int ordinal,
        int total,
        IProgress<GeoFileDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(definition.DownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(definition.DownloadUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GeoFiles 下载地址不在允许的 HTTPS 源中。");
        }

        progress?.Report(new GeoFileDownloadProgress(definition, ordinal, total, 0, "正在连接"));
        using var request = new HttpRequestMessage(HttpMethod.Get, definition.DownloadUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            throw new InvalidDataException("GeoFiles 下载源返回了未允许的重定向。");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength &&
            (contentLength <= 0 || contentLength > MaximumFileLength))
        {
            throw new InvalidDataException("GeoFiles 文件大小异常，已取消写入。");
        }

        var targetPath = GetLocalPath(definition);
        var temporaryPath = Path.Combine(_paths.GeoFilesDirectory, $".{definition.FileName}.{Guid.NewGuid():N}.tmp");
        long copied = 0;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    copied += read;
                    if (copied > MaximumFileLength)
                    {
                        throw new InvalidDataException("GeoFiles 文件超过安全大小限制，已取消写入。");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    var percent = response.Content.Headers.ContentLength is { } length && length > 0
                        ? (int)Math.Clamp(copied * 100 / length, 0, 100)
                        : 0;
                    progress?.Report(new GeoFileDownloadProgress(definition, ordinal, total, percent, "正在下载"));
                }

                await output.FlushAsync(cancellationToken);
            }
            if (copied < 64)
            {
                throw new InvalidDataException("GeoFiles 文件内容异常，已取消写入。");
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            state.Files[definition.Id] = new GeoFileInstallInfo
            {
                Id = definition.Id,
                FileName = definition.FileName,
                Size = copied,
                ETag = response.Headers.ETag?.Tag,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            progress?.Report(new GeoFileDownloadProgress(definition, ordinal, total, 100, "已更新"));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private bool IsAvailable(GeoFileDefinition definition)
    {
        try
        {
            var info = new FileInfo(GetLocalPath(definition));
            return info.Exists && info.Length >= 64 && info.Length <= MaximumFileLength;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed record GeoFileDownloadProgress(
    GeoFileDefinition Definition,
    int Ordinal,
    int Total,
    int Percent,
    string Status);
