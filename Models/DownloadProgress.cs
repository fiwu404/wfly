namespace WFly.Models;

internal sealed record DownloadProgress(string Stage, long ReceivedBytes, long? TotalBytes)
{
    public int? Percentage => TotalBytes is > 0
        ? (int)Math.Clamp(ReceivedBytes * 100 / TotalBytes.Value, 0, 100)
        : null;
}
