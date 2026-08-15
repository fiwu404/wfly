using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace WFly.Models;

/// <summary>
/// Describes one downloadable proxy core and how WFly starts it.
/// </summary>
public sealed class CoreDefinition
{
    private readonly Func<string, IReadOnlyList<string>> _startArgumentsFactory;

    public CoreDefinition(
        string id,
        string displayName,
        string gitHubOwner,
        string gitHubRepository,
        Regex assetNamePattern,
        string executableName,
        Func<string, IReadOnlyList<string>> startArgumentsFactory)
    {
        Id = RequireValue(id, nameof(id));
        DisplayName = RequireValue(displayName, nameof(displayName));
        GitHubOwner = RequireGitHubSegment(gitHubOwner, nameof(gitHubOwner));
        GitHubRepository = RequireGitHubSegment(gitHubRepository, nameof(gitHubRepository));
        AssetNamePattern = assetNamePattern ?? throw new ArgumentNullException(nameof(assetNamePattern));
        ExecutableName = RequireExecutableName(executableName);
        _startArgumentsFactory = startArgumentsFactory ?? throw new ArgumentNullException(nameof(startArgumentsFactory));
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string GitHubOwner { get; }

    public string GitHubRepository { get; }

    /// <summary>
    /// Matches the sole Windows x64 ZIP asset accepted for this core.
    /// </summary>
    public Regex AssetNamePattern { get; }

    /// <summary>
    /// The expected executable filename inside the verified archive.
    /// </summary>
    public string ExecutableName { get; }

    public IReadOnlyList<string> BuildStartArguments(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var arguments = _startArgumentsFactory(configPath)
            ?? throw new InvalidOperationException($"Core '{Id}' returned no start arguments.");

        if (arguments.Any(static argument => argument is null))
        {
            throw new InvalidOperationException($"Core '{Id}' returned an invalid start argument.");
        }

        return new ReadOnlyCollection<string>(arguments.ToArray());
    }

    private static string RequireValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string RequireGitHubSegment(string value, string parameterName)
    {
        value = RequireValue(value, parameterName);
        if (value.IndexOfAny(['/', '\\']) >= 0 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A GitHub owner or repository must be one path segment.", parameterName);
        }

        return value;
    }

    private static string RequireExecutableName(string value)
    {
        value = RequireValue(value, nameof(value));
        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            value.IndexOfAny(['/', '\\']) >= 0 ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
        {
            throw new ArgumentException("The core executable must be a filename ending in .exe.", nameof(value));
        }

        return value;
    }
}
