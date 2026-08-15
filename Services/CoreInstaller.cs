using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Downloads a fixed, catalog-approved release asset, verifies it, and installs it
/// without executing archive contents.
/// </summary>
internal sealed class CoreInstaller
{
    private const long MaximumArchiveBytes = 250L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const int MaximumArchiveEntries = 4096;
    private const int MaximumCompressionRatio = 250;
    private const int CopyBufferSize = 128 * 1024;

    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;
    private readonly InstalledCoreStore _installedCoreStore;

    public CoreInstaller(HttpClient httpClient, AppPaths paths, InstalledCoreStore installedCoreStore)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _installedCoreStore = installedCoreStore ?? throw new ArgumentNullException(nameof(installedCoreStore));
    }

    public async Task<InstalledCore> InstallAsync(
        CoreDefinition definition,
        CoreRelease release,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(release);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateRelease(definition, release);
        _paths.EnsureDirectories();

        var versionDirectoryName = ToSafePathSegment(release.Version, "版本号");
        var coreDirectory = Path.Combine(_paths.CoresDirectory, ToSafePathSegment(definition.Id, "内核标识"));
        var finalDirectory = Path.Combine(coreDirectory, versionDirectoryName);
        var archivePath = Path.Combine(_paths.TempDirectory, $"{definition.Id}-{Guid.NewGuid():N}.zip");
        var stagingDirectory = Path.Combine(coreDirectory, $".install-{Guid.NewGuid():N}");

        Directory.CreateDirectory(coreDirectory);
        if (Directory.Exists(finalDirectory))
        {
            throw new CoreInstallException($"版本 {release.Version} 已存在于本地。为避免覆盖已验证文件，未执行安装。");
        }

        try
        {
            Report(progress, "正在下载官方内核…", 0, release.Asset.Size);
            var actualHash = await DownloadAndHashAsync(release.Asset, archivePath, progress, cancellationToken);
            VerifyHash(release.Asset.Sha256, actualHash);

            Report(progress, "校验通过，正在安全解压…", 0, null);
            Directory.CreateDirectory(stagingDirectory);
            var stagedExecutablePath = await ExtractArchiveAsync(
                archivePath,
                stagingDirectory,
                definition.ExecutableName,
                cancellationToken);
            var relativeExecutablePath = Path.GetRelativePath(stagingDirectory, stagedExecutablePath);

            if (Directory.Exists(finalDirectory))
            {
                throw new CoreInstallException($"版本 {release.Version} 在安装期间已被创建，已安全中止。");
            }

            Directory.Move(stagingDirectory, finalDirectory);
            var installedCore = new InstalledCore
            {
                Id = definition.Id,
                Version = release.Version,
                ExecutablePath = Path.Combine(finalDirectory, relativeExecutablePath),
                ArchiveSha256 = actualHash,
                InstalledAt = DateTimeOffset.UtcNow,
            };

            await _installedCoreStore.RecordAsync(installedCore, cancellationToken);
            Report(progress, "内核已安装并验证。", release.Asset.Size, release.Asset.Size);
            return installedCore;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoreInstallException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new CoreInstallException("安装内核时发生文件系统错误。", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new CoreInstallException("内核归档无效或不安全，已拒绝安装。", exception);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async Task<string> DownloadAndHashAsync(
        CoreReleaseAsset asset,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithTrustedRedirectsAsync(asset.DownloadUrl, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new CoreInstallException($"官方内核下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.Size)
        {
            throw new CoreInstallException("下载响应的文件大小与官方 Release 元数据不一致。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[CopyBufferSize];
        long received = 0;
        long lastReported = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            received += read;
            if (received > asset.Size || received > MaximumArchiveBytes)
            {
                throw new CoreInstallException("下载文件超过允许大小，已中止。");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            if (received - lastReported >= 256 * 1024 || received == asset.Size)
            {
                Report(progress, "正在下载官方内核…", received, asset.Size);
                lastReported = received;
            }
        }

        if (received != asset.Size)
        {
            throw new CoreInstallException("下载文件大小与官方 Release 元数据不一致。");
        }

        await output.FlushAsync(cancellationToken);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<string> ExtractArchiveAsync(
        string archivePath,
        string stagingDirectory,
        string executableName,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(stagingDirectory);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalExpanded = 0;
        var entryCount = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > MaximumArchiveEntries)
            {
                throw new InvalidDataException("归档包含过多文件。");
            }

            RejectSymbolicLink(entry);
            var destination = GetSafeDestination(rootPrefix, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            if (!extractedPaths.Add(destination))
            {
                throw new InvalidDataException("归档包含重复或大小写冲突的文件路径。");
            }

            ValidateEntrySize(entry, ref totalExpanded);
            var destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException("归档文件路径无效。");
            Directory.CreateDirectory(destinationDirectory);

            await using var input = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyEntryAsync(input, output, entry.Length, cancellationToken);
        }

        var executables = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), executableName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return executables.Length switch
        {
            1 => executables[0],
            0 => throw new InvalidDataException($"归档中未找到预期的 {executableName}。"),
            _ => throw new InvalidDataException($"归档中包含多个 {executableName}，已拒绝安装。"),
        };
    }

    private async Task<HttpResponseMessage> SendWithTrustedRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var nextUri = response.Headers.Location;
            if (nextUri is null)
            {
                response.Dispose();
                throw new CoreInstallException("官方下载返回了没有地址的重定向。");
            }

            var resolvedUri = nextUri.IsAbsoluteUri ? nextUri : new Uri(currentUri, nextUri);
            response.Dispose();
            if (!IsTrustedDownloadHost(resolvedUri))
            {
                throw new CoreInstallException("官方下载重定向到了不受信任的主机，已拒绝下载。");
            }

            currentUri = resolvedUri;
        }

        throw new CoreInstallException("官方下载重定向次数过多，已中止。");
    }

    private static void ValidateRelease(CoreDefinition definition, CoreRelease release)
    {
        if (!string.Equals(release.Definition.Id, definition.Id, StringComparison.Ordinal) ||
            !string.Equals(release.Definition.GitHubOwner, definition.GitHubOwner, StringComparison.Ordinal) ||
            !string.Equals(release.Definition.GitHubRepository, definition.GitHubRepository, StringComparison.Ordinal))
        {
            throw new CoreInstallException("发布元数据与所选内核不匹配，已拒绝安装。");
        }

        if (!definition.AssetNamePattern.IsMatch(release.Asset.Name))
        {
            throw new CoreInstallException("发布资产不符合受支持的 Windows x64 规则，已拒绝安装。");
        }

        if (release.Asset.Size > MaximumArchiveBytes)
        {
            throw new CoreInstallException($"内核归档超过 {MaximumArchiveBytes / 1024 / 1024} MB 限制。");
        }

        if (!IsTrustedDownloadHost(release.Asset.DownloadUrl) ||
            !string.Equals(release.Asset.DownloadUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new CoreInstallException("发布资产不属于允许的官方下载地址。");
        }
    }

    private static void VerifyHash(string expectedHash, string actualHash)
    {
        var expectedBytes = Convert.FromHexString(expectedHash);
        var actualBytes = Convert.FromHexString(actualHash);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new CoreInstallException("SHA-256 校验失败，归档未安装。");
        }
    }

    private static void ValidateEntrySize(ZipArchiveEntry entry, ref long totalExpanded)
    {
        if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - totalExpanded)
        {
            throw new InvalidDataException("归档解压后的总大小超过限制。");
        }

        if (entry.Length > 0)
        {
            if (entry.CompressedLength == 0 || entry.Length / Math.Max(entry.CompressedLength, 1) > MaximumCompressionRatio)
            {
                throw new InvalidDataException("归档包含可疑的高压缩比文件。");
            }
        }

        totalExpanded += entry.Length;
    }

    private static void RejectSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixMode == UnixSymbolicLink)
        {
            throw new InvalidDataException("归档包含符号链接，已拒绝安装。");
        }
    }

    private static string GetSafeDestination(string rootPrefix, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) ||
            entryName.IndexOf('\0') >= 0 ||
            Path.IsPathRooted(entryName))
        {
            throw new InvalidDataException("归档包含无效路径。");
        }

        var normalized = entryName
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (normalized.Length == 0)
        {
            throw new InvalidDataException("归档包含无效路径。");
        }
        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':') || segment.Any(char.IsControl)))
        {
            throw new InvalidDataException("归档包含不安全的文件路径。");
        }

        var destination = Path.GetFullPath(Path.Combine(rootPrefix, normalized));
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("归档试图写入目标目录之外。");
        }

        return destination;
    }

    private static async Task CopyEntryAsync(Stream input, Stream output, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            copied += read;
            if (copied > expectedLength)
            {
                throw new InvalidDataException("归档文件内容长度不一致。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (copied != expectedLength)
        {
            throw new InvalidDataException("归档文件内容长度不一致。");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsTrustedDownloadHost(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToSafePathSegment(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_' and not '+'))
        {
            throw new CoreInstallException($"{fieldName}包含不安全字符，已拒绝安装。");
        }

        return value;
    }

    private static void Report(IProgress<DownloadProgress>? progress, string stage, long receivedBytes, long? totalBytes) =>
        progress?.Report(new DownloadProgress(stage, receivedBytes, totalBytes));

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temporary cleanup must not hide the original install error.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary cleanup must not hide the original install error.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temporary cleanup must not hide the original install error.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary cleanup must not hide the original install error.
        }
    }
}

internal sealed class CoreInstallException : Exception
{
    public CoreInstallException(string message)
        : base(message)
    {
    }

    public CoreInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
