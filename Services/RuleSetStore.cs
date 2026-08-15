using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Durable store for editable graphical rules and their optional raw
/// core-specific JSON counterparts.
/// </summary>
internal sealed class RuleSetStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RuleSetStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<IReadOnlyList<RuleSet>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.RuleSets
                .OrderBy(ruleSet => ruleSet.CreatedAt)
                .ThenBy(ruleSet => ruleSet.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuleSet?> GetAsync(string ruleSetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.RuleSets
                .FirstOrDefault(ruleSet => string.Equals(ruleSet.Id, ruleSetId, StringComparison.Ordinal)) is { } ruleSet
                ? Clone(ruleSet)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuleSet> SaveAsync(RuleSet ruleSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var id = string.IsNullOrWhiteSpace(ruleSet.Id) ? StoreValidation.NewId() : ruleSet.Id.Trim();
            var existing = state.RuleSets.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            var normalized = Normalize(ruleSet, id, existing?.CreatedAt);

            if (existing is null)
            {
                state.RuleSets.Add(normalized);
            }
            else
            {
                state.RuleSets[state.RuleSets.IndexOf(existing)] = normalized;
            }

            await JsonStore.WriteAtomicallyAsync(_paths.RuleSetsFile, state, cancellationToken);
            return Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string ruleSetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var removed = state.RuleSets.RemoveAll(ruleSet => string.Equals(ruleSet.Id, ruleSetId, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            await JsonStore.WriteAtomicallyAsync(_paths.RuleSetsFile, state, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<RuleSetsState> LoadStateAsync(CancellationToken cancellationToken) =>
        JsonStore.ReadOrDefaultAsync(_paths.RuleSetsFile, static () => new RuleSetsState(), cancellationToken);

    private static RuleSet Normalize(RuleSet source, string id, DateTimeOffset? createdAt)
    {
        var seenEntryIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedEntries = new List<RuleEntry>();
        foreach (var entry in source.Entries ?? [])
        {
            if (entry is null)
            {
                continue;
            }

            var entryId = string.IsNullOrWhiteSpace(entry.Id) ? StoreValidation.NewId() : entry.Id.Trim();
            if (!seenEntryIds.Add(entryId))
            {
                throw new ArgumentException("Rule entry IDs must be unique.", nameof(source.Entries));
            }

            normalizedEntries.Add(NormalizeEntry(entry, entryId));
        }

        var now = DateTimeOffset.UtcNow;
        return new RuleSet
        {
            Id = StoreValidation.RequiredText(id, nameof(source.Id), 128),
            Name = StoreValidation.RequiredText(source.Name, nameof(source.Name), 256),
            CoreId = StoreValidation.RequiredText(source.CoreId, nameof(source.CoreId), 128),
            IsEnabled = source.IsEnabled,
            Entries = normalizedEntries,
            ConfigurationJson = StoreValidation.OptionalText(source.ConfigurationJson, 1_048_576),
            CreatedAt = createdAt ?? (source.CreatedAt == default ? now : source.CreatedAt),
            UpdatedAt = now,
        };
    }

    private static RuleEntry NormalizeEntry(RuleEntry source, string id)
    {
        if (source.Priority is < -1_000_000 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(source.Priority), "Rule priority must be between -1,000,000 and 1,000,000.");
        }

        var matchKind = StoreValidation.RequiredText(source.MatchKind, nameof(source.MatchKind), 128);
        return new RuleEntry
        {
            Id = StoreValidation.RequiredText(id, nameof(source.Id), 128),
            Name = StoreValidation.OptionalText(source.Name, 256) ?? matchKind,
            MatchKind = matchKind,
            MatchValue = StoreValidation.OptionalText(source.MatchValue, 32_768) ?? string.Empty,
            Action = StoreValidation.RequiredText(source.Action, nameof(source.Action), 128),
            OutboundTag = StoreValidation.OptionalText(source.OutboundTag, 256),
            IsEnabled = source.IsEnabled,
            Priority = source.Priority,
            ConfigurationJson = StoreValidation.OptionalText(source.ConfigurationJson, 1_048_576),
        };
    }

    private static RuleSet Clone(RuleSet source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        CoreId = source.CoreId,
        IsEnabled = source.IsEnabled,
        Entries = (source.Entries ?? [])
            .Where(static entry => entry is not null)
            .Select(CloneEntry)
            .ToList(),
        ConfigurationJson = source.ConfigurationJson,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };

    private static RuleEntry CloneEntry(RuleEntry source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        MatchKind = source.MatchKind,
        MatchValue = source.MatchValue,
        Action = source.Action,
        OutboundTag = source.OutboundTag,
        IsEnabled = source.IsEnabled,
        Priority = source.Priority,
        ConfigurationJson = source.ConfigurationJson,
    };
}
