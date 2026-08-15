namespace WFly.Models;

internal sealed class AppSettings
{
    public string SelectedCoreId { get; set; } = "sing-box";
    public string? ConfigPath { get; set; }
}
