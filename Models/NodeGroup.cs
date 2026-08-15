namespace WFly.Models;

/// <summary>
/// A user-created node group. A group may exist without a subscription so that
/// manually added nodes always have an explicit home.
/// </summary>
internal sealed class NodeGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Subscription URL, when this is a subscription-backed group. It can be
    /// null or blank for a manually managed group.
    /// </summary>
    public string? SubscriptionUrl { get; set; }

    /// <summary>Core selected or detected for this group's nodes.</summary>
    public string CoreId { get; set; } = "sing-box";

    /// <summary>
    /// Subscription refresh interval in hours. <see langword="null"/> means
    /// that automatic refresh is disabled.
    /// </summary>
    public int? UpdateIntervalHours { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }
    public string? LastUpdateError { get; set; }
}

internal sealed class NodeGroupsState
{
    public List<NodeGroup> Groups { get; set; } = [];
}
