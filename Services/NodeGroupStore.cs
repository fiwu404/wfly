using WFly.Models;

namespace WFly.Services;

/// <summary>Durable CRUD store for subscription-backed and manual node groups.</summary>
internal sealed class NodeGroupStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NodeGroupStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<IReadOnlyList<NodeGroup>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Groups
                .OrderBy(group => group.CreatedAt)
                .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NodeGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Groups
                .FirstOrDefault(group => string.Equals(group.Id, groupId, StringComparison.Ordinal)) is { } group
                ? Clone(group)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NodeGroup> SaveAsync(NodeGroup nodeGroup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeGroup);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var id = string.IsNullOrWhiteSpace(nodeGroup.Id) ? StoreValidation.NewId() : nodeGroup.Id.Trim();
            var existing = state.Groups.FirstOrDefault(group => string.Equals(group.Id, id, StringComparison.Ordinal));
            var normalized = Normalize(nodeGroup, id, existing?.CreatedAt);

            if (existing is null)
            {
                state.Groups.Add(normalized);
            }
            else
            {
                var index = state.Groups.IndexOf(existing);
                state.Groups[index] = normalized;
            }

            await JsonStore.WriteAtomicallyAsync(_paths.NodeGroupsFile, state, cancellationToken);
            return Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var removed = state.Groups.RemoveAll(group => string.Equals(group.Id, groupId, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            await JsonStore.WriteAtomicallyAsync(_paths.NodeGroupsFile, state, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordRefreshResultAsync(
        string groupId,
        DateTimeOffset completedAt,
        string? error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var existing = state.Groups.FirstOrDefault(group => string.Equals(group.Id, groupId, StringComparison.Ordinal));
            if (existing is null)
            {
                throw new KeyNotFoundException($"Node group '{groupId}' was not found.");
            }

            existing.LastUpdatedAt = completedAt;
            existing.LastUpdateError = StoreValidation.OptionalText(error, 4_096);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await JsonStore.WriteAtomicallyAsync(_paths.NodeGroupsFile, state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<NodeGroupsState> LoadStateAsync(CancellationToken cancellationToken) =>
        JsonStore.ReadOrDefaultAsync(_paths.NodeGroupsFile, static () => new NodeGroupsState(), cancellationToken);

    private static NodeGroup Normalize(NodeGroup source, string id, DateTimeOffset? createdAt)
    {
        var subscriptionUrl = StoreValidation.OptionalText(source.SubscriptionUrl, 8_192);
        // A missing interval explicitly means that automatic refresh is disabled.
        // The UI assigns the six-hour default when a new subscription is entered;
        // the store must preserve a user's subsequent choice of "不更新".
        int? refreshHours = subscriptionUrl is null
            ? null
            : source.UpdateIntervalHours;
        if (refreshHours is <= 0 or > 24 * 31)
        {
            throw new ArgumentOutOfRangeException(nameof(source.UpdateIntervalHours), "Refresh interval must be between 1 and 744 hours.");
        }

        var now = DateTimeOffset.UtcNow;
        return new NodeGroup
        {
            Id = StoreValidation.RequiredText(id, nameof(source.Id), 128),
            Name = StoreValidation.RequiredText(source.Name, nameof(source.Name), 256),
            SubscriptionUrl = subscriptionUrl,
            CoreId = StoreValidation.RequiredText(source.CoreId, nameof(source.CoreId), 128),
            UpdateIntervalHours = refreshHours,
            CreatedAt = createdAt ?? (source.CreatedAt == default ? now : source.CreatedAt),
            UpdatedAt = now,
            LastUpdatedAt = source.LastUpdatedAt,
            LastUpdateError = StoreValidation.OptionalText(source.LastUpdateError, 4_096),
        };
    }

    private static NodeGroup Clone(NodeGroup source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        SubscriptionUrl = source.SubscriptionUrl,
        CoreId = source.CoreId,
        UpdateIntervalHours = source.UpdateIntervalHours,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        LastUpdatedAt = source.LastUpdatedAt,
        LastUpdateError = source.LastUpdateError,
    };
}
