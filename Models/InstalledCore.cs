namespace WFly.Models;

internal sealed class InstalledCore
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ArchiveSha256 { get; set; } = string.Empty;
    public DateTimeOffset InstalledAt { get; set; }
}
