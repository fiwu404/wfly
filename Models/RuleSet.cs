namespace WFly.Models;

/// <summary>
/// A graphical rule collection that can later be rendered to a core-specific
/// configuration file. The matching and action fields remain text-based so an
/// advanced editor can preserve unsupported future rule kinds.
/// </summary>
internal sealed class RuleSet
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CoreId { get; set; } = "sing-box";
    public bool IsEnabled { get; set; } = true;
    public List<RuleEntry> Entries { get; set; } = [];

    /// <summary>
    /// Optional full core-specific rule fragment for the configuration-file
    /// editor. It is kept alongside the graphical entries rather than forcing
    /// an editor to discard fields it does not yet understand.
    /// </summary>
    public string? ConfigurationJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RuleEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string MatchKind { get; set; } = "domain_suffix";
    public string MatchValue { get; set; } = string.Empty;
    public string Action { get; set; } = "proxy";
    public string? OutboundTag { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; }

    /// <summary>
    /// Optional raw core-specific JSON for an advanced configuration-file
    /// editor. The graphical fields are still available for ordinary rules.
    /// </summary>
    public string? ConfigurationJson { get; set; }
}

internal sealed class RuleSetsState
{
    public List<RuleSet> RuleSets { get; set; } = [];
}
