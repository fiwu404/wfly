using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Durable node store. Consumers can pass a <see cref="NodeGroupStore"/> to
/// enforce the invariant that a node cannot be saved without a node group.
/// </summary>
internal sealed class ProxyNodeStore
{
    private readonly AppPaths _paths;
    private readonly NodeGroupStore? _nodeGroupStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProxyNodeStore(AppPaths paths, NodeGroupStore? nodeGroupStore = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _nodeGroupStore = nodeGroupStore;
    }

    public async Task<IReadOnlyList<ProxyNode>> GetByGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Nodes
                .Where(node => string.Equals(node.GroupId, groupId, StringComparison.Ordinal))
                .OrderBy(node => node.CreatedAt)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns a detached, stable snapshot across every node group.  The tray
    /// uses this cache so opening its menu never has to synchronously read the
    /// state file once per group.
    /// </summary>
    public async Task<IReadOnlyList<ProxyNode>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Nodes
                .OrderBy(node => node.CreatedAt)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProxyNode?> GetAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Nodes
                .FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal)) is { } node
                ? Clone(node)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProxyNode> SaveAsync(ProxyNode proxyNode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxyNode);
        var groupId = StoreValidation.RequiredText(proxyNode.GroupId, nameof(proxyNode.GroupId), 128);
        await EnsureNodeGroupExistsAsync(groupId, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var id = string.IsNullOrWhiteSpace(proxyNode.Id) ? StoreValidation.NewId() : proxyNode.Id.Trim();
            var existing = state.Nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal));
            var normalized = Normalize(proxyNode, id, groupId, existing?.CreatedAt);

            if (existing is null)
            {
                state.Nodes.Add(normalized);
            }
            else
            {
                state.Nodes[state.Nodes.IndexOf(existing)] = normalized;
            }

            await JsonStore.WriteAtomicallyAsync(_paths.ProxyNodesFile, state, cancellationToken);
            return Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically replaces every node in one existing group. Subscription
    /// refreshes use this instead of deleting and adding nodes one at a time,
    /// so an interrupted update leaves the prior group intact.
    /// </summary>
    public async Task<IReadOnlyList<ProxyNode>> ReplaceForGroupAsync(
        string groupId,
        IEnumerable<ProxyNode> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(nodes);
        var normalizedGroupId = StoreValidation.RequiredText(groupId, nameof(groupId), 128);
        var replacements = nodes.ToArray();
        await EnsureNodeGroupExistsAsync(normalizedGroupId, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var existingForGroup = state.Nodes
                .Where(node => string.Equals(node.GroupId, normalizedGroupId, StringComparison.Ordinal))
                .ToDictionary(node => node.Id, StringComparer.Ordinal);
            var idsUsedByOtherGroups = state.Nodes
                .Where(node => !string.Equals(node.GroupId, normalizedGroupId, StringComparison.Ordinal))
                .Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);
            var replacementIds = new HashSet<string>(StringComparer.Ordinal);
            var normalizedNodes = new List<ProxyNode>(replacements.Length);

            foreach (var replacement in replacements)
            {
                ArgumentNullException.ThrowIfNull(replacement);
                if (!string.IsNullOrWhiteSpace(replacement.GroupId) &&
                    !string.Equals(replacement.GroupId, normalizedGroupId, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Every replacement node must belong to the requested group.", nameof(nodes));
                }

                var id = string.IsNullOrWhiteSpace(replacement.Id) ? StoreValidation.NewId() : replacement.Id.Trim();
                if (!replacementIds.Add(id))
                {
                    throw new ArgumentException("Replacement node IDs must be unique.", nameof(nodes));
                }

                if (idsUsedByOtherGroups.Contains(id))
                {
                    throw new ArgumentException("A replacement node ID is already used by another group.", nameof(nodes));
                }

                existingForGroup.TryGetValue(id, out var existing);
                normalizedNodes.Add(Normalize(replacement, id, normalizedGroupId, existing?.CreatedAt));
            }

            state.Nodes.RemoveAll(node => string.Equals(node.GroupId, normalizedGroupId, StringComparison.Ordinal));
            state.Nodes.AddRange(normalizedNodes);
            await JsonStore.WriteAtomicallyAsync(_paths.ProxyNodesFile, state, cancellationToken);
            return normalizedNodes.Select(Clone).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var removed = state.Nodes.RemoveAll(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            await JsonStore.WriteAtomicallyAsync(_paths.ProxyNodesFile, state, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes all nodes belonging to a group. It is deliberately explicit so
    /// the caller can ask for confirmation before a group is deleted.
    /// </summary>
    public async Task<int> DeleteByGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var removed = state.Nodes.RemoveAll(node => string.Equals(node.GroupId, groupId, StringComparison.Ordinal));
            if (removed > 0)
            {
                await JsonStore.WriteAtomicallyAsync(_paths.ProxyNodesFile, state, cancellationToken);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<ProxyNodesState> LoadStateAsync(CancellationToken cancellationToken) =>
        JsonStore.ReadOrDefaultAsync(_paths.ProxyNodesFile, static () => new ProxyNodesState(), cancellationToken);

    private async Task EnsureNodeGroupExistsAsync(string groupId, CancellationToken cancellationToken)
    {
        if (_nodeGroupStore is not null)
        {
            if (await _nodeGroupStore.GetAsync(groupId, cancellationToken) is not null)
            {
                return;
            }
        }
        else
        {
            var state = await JsonStore.ReadOrDefaultAsync(
                _paths.NodeGroupsFile,
                static () => new NodeGroupsState(),
                cancellationToken);
            if (state.Groups.Any(group => string.Equals(group.Id, groupId, StringComparison.Ordinal)))
            {
                return;
            }
        }

        throw new InvalidOperationException("Create a node group before adding a node.");
    }

    private static ProxyNode Normalize(ProxyNode source, string id, string groupId, DateTimeOffset? createdAt)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProxyNode
        {
            Id = StoreValidation.RequiredText(id, nameof(source.Id), 128),
            GroupId = groupId,
            Name = StoreValidation.RequiredText(source.Name, nameof(source.Name), 256),
            Protocol = StoreValidation.RequiredText(source.Protocol, nameof(source.Protocol), 128),
            CoreId = StoreValidation.RequiredText(source.CoreId, nameof(source.CoreId), 128),
            ShareLink = StoreValidation.OptionalText(source.ShareLink, 32_768),
            ConfigurationJson = StoreValidation.OptionalText(source.ConfigurationJson, 1_048_576),
            ManualOptionsJson = StoreValidation.OptionalText(source.ManualOptionsJson, 1_048_576),
            PingResult = StoreValidation.OptionalText(source.PingResult, 64),
            TcpingResult = StoreValidation.OptionalText(source.TcpingResult, 64),
            RealConnectionResult = StoreValidation.OptionalText(source.RealConnectionResult, 64),
            UdpResult = StoreValidation.OptionalText(source.UdpResult, 64),
            LastTestedAt = source.LastTestedAt,
            IsEnabled = source.IsEnabled,
            CreatedAt = createdAt ?? (source.CreatedAt == default ? now : source.CreatedAt),
            UpdatedAt = now,
        };
    }

    private static ProxyNode Clone(ProxyNode source) => new()
    {
        Id = source.Id,
        GroupId = source.GroupId,
        Name = source.Name,
        Protocol = source.Protocol,
        CoreId = source.CoreId,
        ShareLink = source.ShareLink,
        ConfigurationJson = source.ConfigurationJson,
        ManualOptionsJson = source.ManualOptionsJson,
        PingResult = source.PingResult,
        TcpingResult = source.TcpingResult,
        RealConnectionResult = source.RealConnectionResult,
        UdpResult = source.UdpResult,
        LastTestedAt = source.LastTestedAt,
        IsEnabled = source.IsEnabled,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };
}
