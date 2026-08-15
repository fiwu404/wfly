using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace WFly.Models;

/// <summary>
/// The fixed set of core sources accepted by the first Windows x64 release.
/// </summary>
public static class CoreRegistry
{
    private static readonly IReadOnlyList<CoreDefinition> Definitions =
        new ReadOnlyCollection<CoreDefinition>(
        [
            new CoreDefinition(
                id: "sing-box",
                displayName: "sing-box",
                gitHubOwner: "SagerNet",
                gitHubRepository: "sing-box",
                assetNamePattern: CreateAssetPattern(@"\Asing-box-(?<version>[A-Za-z0-9][A-Za-z0-9._+\-]*)-windows-amd64\.zip\z"),
                executableName: "sing-box.exe",
                startArgumentsFactory: static configPath => ["run", "-c", configPath]),
            new CoreDefinition(
                id: "xray-core",
                displayName: "Xray-core",
                gitHubOwner: "XTLS",
                gitHubRepository: "Xray-core",
                assetNamePattern: CreateAssetPattern(@"\AXray-windows-64\.zip\z"),
                executableName: "xray.exe",
                startArgumentsFactory: static configPath => ["run", "-c", configPath]),
        ]);

    public static IReadOnlyList<CoreDefinition> All => Definitions;

    public static CoreDefinition? GetById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Definitions.FirstOrDefault(definition =>
            string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static Regex CreateAssetPattern(string pattern) => new(
        pattern,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));
}
