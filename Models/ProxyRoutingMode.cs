namespace WFly.Models;

/// <summary>
/// Chooses how traffic is routed after a proxy core has been started.
/// Rules applies the user's graphical/JSON rule sets, Global sends all
/// traffic through the selected node, and Direct keeps all traffic local.
/// </summary>
internal enum ProxyRoutingMode
{
    Rules = 0,
    Global = 1,
    Direct = 2,
}
