using System.Text.RegularExpressions;
using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Process and connection log buffer. It deliberately has no file writer:
/// the UI is the only place that can persist a copy after an explicit export.
/// </summary>
internal sealed class InMemoryLogStore
{
    private const int MaximumEntries = 10_000;
    private readonly object _sync = new();
    private readonly Queue<RuntimeLogEntry> _entries = new();

    public event Action<RuntimeLogEntry>? EntryAdded;

    public IReadOnlyList<RuntimeLogEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    public void Add(CoreLogEntry entry) =>
        Add(entry.Timestamp, entry.Stream, entry.Message);

    public void AddInfo(string category, string message) =>
        Add(DateTimeOffset.Now, category, message);

    public void Add(DateTimeOffset timestamp, string category, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new RuntimeLogEntry(
            timestamp,
            string.IsNullOrWhiteSpace(category) ? "SYS" : category.Trim(),
            RedactUrlSecrets(message.Trim()));
        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaximumEntries)
            {
                _entries.Dequeue();
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    private static string RedactUrlSecrets(string input) => UrlPattern.Replace(input, static match =>
    {
        if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri))
        {
            return match.Value;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.GetLeftPart(UriPartial.Path) + "?已隐藏";
    });

    private static readonly Regex UrlPattern = new(
        @"https?://[^\s\""'<>]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));
}

internal sealed record RuntimeLogEntry(DateTimeOffset Timestamp, string Category, string Message)
{
    public string DisplayText => $"{Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} [{Category}] {Message}";
}
