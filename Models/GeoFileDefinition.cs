namespace WFly.Models;

/// <summary>
/// A maintained sing-box binary rule-set used by the built-in smart routing
/// policy. The source URLs are fixed by the application and are never built
/// from user input.
/// </summary>
internal sealed record GeoFileDefinition(
    string Id,
    string DisplayName,
    string FileName,
    Uri DownloadUri);

internal sealed class GeoFileState
{
    public Dictionary<string, GeoFileInstallInfo> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class GeoFileInstallInfo
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal static class GeoFileRegistry
{
    /// <summary>
    /// Kept deliberately small: these two upstream rule-sets reproduce the
    /// familiar "China domains/IPs direct, all other traffic through proxy"
    /// policy without making every profile download a large rule catalog.
    /// </summary>
    public static IReadOnlyList<GeoFileDefinition> SmartRouting { get; } =
    [
        new(
            "geosite-geolocation-cn",
            "GeoSite 中国大陆域名",
            "geosite-geolocation-cn.srs",
            new Uri("https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-geolocation-cn.srs")),
        new(
            "geoip-cn",
            "GeoIP 中国大陆地址",
            "geoip-cn.srs",
            new Uri("https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-cn.srs")),
    ];
}
