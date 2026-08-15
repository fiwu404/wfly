namespace WFly.Models;

/// <summary>
/// A sampled traffic counter set used by the home-page chart. Values are byte
/// counts collected during one interval, not formatted display strings.
/// </summary>
internal sealed class TrafficSnapshot
{
    public DateTimeOffset CapturedAt { get; set; }
    public long ProxyUploadBytes { get; set; }
    public long ProxyDownloadBytes { get; set; }
    public long DirectUploadBytes { get; set; }
    public long DirectDownloadBytes { get; set; }
    public int ActiveConnections { get; set; }
}

internal sealed class TrafficSnapshotsState
{
    public List<TrafficSnapshot> Snapshots { get; set; } = [];
}
