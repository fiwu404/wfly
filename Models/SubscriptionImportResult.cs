namespace WFly.Models;

internal sealed record SubscriptionImportResult(
    string ConfigPath,
    int ImportedCount,
    int SkippedCount,
    string SourceHost);
