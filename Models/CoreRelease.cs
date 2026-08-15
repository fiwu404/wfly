namespace WFly.Models;

/// <summary>
/// The latest stable release metadata selected for a core.
/// </summary>
public sealed class CoreRelease
{
    public CoreRelease(CoreDefinition definition, string version, CoreReleaseAsset asset)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Any(char.IsControl))
        {
            throw new ArgumentException("The release version cannot contain control characters.", nameof(version));
        }

        Version = version.Trim();
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
    }

    public CoreDefinition Definition { get; }

    /// <summary>
    /// The GitHub release tag, retained verbatim apart from surrounding whitespace.
    /// </summary>
    public string Version { get; }

    public CoreReleaseAsset Asset { get; }
}

/// <summary>
/// A verified-to-be-eligible GitHub release asset. <see cref="Sha256"/> is a
/// lowercase, 64-character hexadecimal SHA-256 value without the <c>sha256:</c> prefix.
/// </summary>
public sealed class CoreReleaseAsset
{
    public CoreReleaseAsset(string name, Uri downloadUrl, string sha256, long size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['/', '\\']) >= 0 || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The release asset must be a ZIP filename.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(downloadUrl);
        if (!downloadUrl.IsAbsoluteUri ||
            !string.Equals(downloadUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(downloadUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(downloadUrl.UserInfo))
        {
            throw new ArgumentException("The release download URL must be an HTTPS URL on github.com.", nameof(downloadUrl));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (!IsSha256(sha256))
        {
            throw new ArgumentException("The release SHA-256 must be exactly 64 hexadecimal characters.", nameof(sha256));
        }

        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The release asset size must be positive.");
        }

        Name = name;
        DownloadUrl = downloadUrl;
        Sha256 = sha256.ToLowerInvariant();
        Size = size;
    }

    public string Name { get; }

    public Uri DownloadUrl { get; }

    public string Sha256 { get; }

    public long Size { get; }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}
