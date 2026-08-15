using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Reads release metadata from GitHub and selects the one verified Windows x64 archive
/// permitted by a <see cref="CoreDefinition"/>.
/// </summary>
public sealed class CoreCatalogService : IDisposable
{
    private static readonly Uri GitHubApiBaseUri = new("https://api.github.com/");
    private static readonly Regex Sha256DigestPattern = new(
        @"\Asha256:([A-Fa-f0-9]{64})\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public CoreCatalogService()
        : this(HttpClientFactory.Create(), ownsHttpClient: true)
    {
    }

    public CoreCatalogService(HttpClient httpClient)
        : this(httpClient, ownsHttpClient: false)
    {
    }

    private CoreCatalogService(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<CoreRelease> GetLatestAsync(
        CoreDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var requestUri = BuildLatestReleaseUri(definition);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.UserAgent.ParseAdd(ProductInfo.UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailure(definition, response.StatusCode, response.ReasonPhrase);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            GitHubReleaseDto? release;
            try
            {
                release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new CoreCatalogException(
                    $"GitHub returned invalid release metadata for {definition.DisplayName}.",
                    exception);
            }

            if (release is null)
            {
                throw new CoreCatalogException($"GitHub returned empty release metadata for {definition.DisplayName}.");
            }

            return BuildCoreRelease(definition, release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoreCatalogException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CoreCatalogException(
                $"Could not query the GitHub release metadata for {definition.DisplayName}.",
                exception);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static Uri BuildLatestReleaseUri(CoreDefinition definition)
    {
        var owner = Uri.EscapeDataString(definition.GitHubOwner);
        var repository = Uri.EscapeDataString(definition.GitHubRepository);
        return new Uri(GitHubApiBaseUri, $"repos/{owner}/{repository}/releases/latest");
    }

    private static CoreCatalogException CreateHttpFailure(
        CoreDefinition definition,
        HttpStatusCode statusCode,
        string? reasonPhrase)
    {
        var suffix = string.IsNullOrWhiteSpace(reasonPhrase) ? string.Empty : $" ({reasonPhrase})";
        return new CoreCatalogException(
            $"GitHub could not provide the latest stable release for {definition.DisplayName}: " +
            $"HTTP {(int)statusCode}{suffix}.");
    }

    private static CoreRelease BuildCoreRelease(CoreDefinition definition, GitHubReleaseDto release)
    {
        if (release.Draft)
        {
            throw new CoreCatalogException($"GitHub returned a draft release for {definition.DisplayName}.");
        }

        if (release.Prerelease)
        {
            throw new CoreCatalogException($"GitHub returned a prerelease for {definition.DisplayName}.");
        }

        if (string.IsNullOrWhiteSpace(release.TagName))
        {
            throw new CoreCatalogException($"GitHub did not provide a release tag for {definition.DisplayName}.");
        }

        var matchingAssets = FindMatchingAssets(definition, release.Assets);
        if (matchingAssets.Count == 0)
        {
            throw new CoreCatalogException(
                $"The latest {definition.DisplayName} release has no supported Windows x64 ZIP asset.");
        }

        if (matchingAssets.Count != 1)
        {
            throw new CoreCatalogException(
                $"The latest {definition.DisplayName} release has multiple supported Windows x64 ZIP assets.");
        }

        var asset = matchingAssets[0];
        var assetName = asset.Name!;
        var downloadUrl = ParseDownloadUrl(definition, asset.BrowserDownloadUrl);
        var sha256 = ParseSha256(definition, asset.Digest);

        if (asset.Size <= 0)
        {
            throw new CoreCatalogException(
                $"The selected {definition.DisplayName} asset has an invalid size.");
        }

        try
        {
            return new CoreRelease(
                definition,
                release.TagName,
                new CoreReleaseAsset(assetName, downloadUrl, sha256, asset.Size));
        }
        catch (ArgumentException exception)
        {
            throw new CoreCatalogException(
                $"GitHub returned invalid release metadata for {definition.DisplayName}.",
                exception);
        }
    }

    private static List<GitHubReleaseAssetDto> FindMatchingAssets(
        CoreDefinition definition,
        IReadOnlyList<GitHubReleaseAssetDto>? assets)
    {
        var matches = new List<GitHubReleaseAssetDto>();
        foreach (var asset in assets ?? Array.Empty<GitHubReleaseAssetDto>())
        {
            if (string.IsNullOrWhiteSpace(asset.Name))
            {
                continue;
            }

            try
            {
                if (definition.AssetNamePattern.IsMatch(asset.Name))
                {
                    if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CoreCatalogException(
                            $"The configured asset rule for {definition.DisplayName} matched a non-ZIP asset.");
                    }

                    matches.Add(asset);
                }
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new CoreCatalogException(
                    $"The asset matching rule timed out for {definition.DisplayName}.",
                    exception);
            }
        }

        return matches;
    }

    private static Uri ParseDownloadUrl(CoreDefinition definition, string? rawDownloadUrl)
    {
        if (!Uri.TryCreate(rawDownloadUrl, UriKind.Absolute, out var downloadUrl) ||
            !string.Equals(downloadUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(downloadUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(downloadUrl.UserInfo))
        {
            throw new CoreCatalogException(
                $"GitHub returned an unsafe download URL for {definition.DisplayName}.");
        }

        return downloadUrl;
    }

    private static string ParseSha256(CoreDefinition definition, string? digest)
    {
        if (digest is null)
        {
            throw new CoreCatalogException(
                $"GitHub did not provide a SHA-256 digest for the selected {definition.DisplayName} asset.");
        }

        var match = Sha256DigestPattern.Match(digest);
        if (!match.Success)
        {
            throw new CoreCatalogException(
                $"GitHub returned an invalid SHA-256 digest for the selected {definition.DisplayName} asset.");
        }

        return match.Groups[1].Value.ToLowerInvariant();
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubReleaseAssetDto>? Assets { get; init; }
    }

    private sealed class GitHubReleaseAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}

/// <summary>
/// Indicates that release discovery failed safely and no asset should be downloaded.
/// </summary>
public sealed class CoreCatalogException : Exception
{
    public CoreCatalogException(string message)
        : base(message)
    {
    }

    public CoreCatalogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
