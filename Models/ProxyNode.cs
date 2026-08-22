namespace WFly.Models;

/// <summary>
/// A single proxy node owned by exactly one <see cref="NodeGroup"/>.
/// ConfigurationJson is intentionally opaque here: each supported core can
/// render its own configuration without losing the original share link.
/// </summary>
internal sealed class ProxyNode
{
    public string Id { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string CoreId { get; set; } = "sing-box";
    public string? ShareLink { get; set; }
    public string? ConfigurationJson { get; set; }
    /// <summary>
    /// WFly's lossless manual-editor state. Runtime profile generation still
    /// consumes <see cref="ConfigurationJson"/>; this payload keeps optional
    /// v2rayN-style fields that a particular core may not currently emit.
    /// </summary>
    public string? ManualOptionsJson { get; set; }
    public string? PingResult { get; set; }
    public string? TcpingResult { get; set; }
    public string? RealConnectionResult { get; set; }
    public string? UdpResult { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ProxyNodesState
{
    public List<ProxyNode> Nodes { get; set; } = [];
}
