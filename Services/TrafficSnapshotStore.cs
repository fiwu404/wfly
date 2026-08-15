using WFly.Models;

namespace WFly.Services;

/// <summary>
/// Bounded, durable history for traffic-chart samples. The caller controls the
/// sampling cadence; this store keeps only the newest samples so data cannot
/// grow without bound.
/// </summary>
internal sealed class TrafficSnapshotStore
{
    public const int DefaultMaximumSnapshots = 4_320;

    private readonly AppPaths _paths;
    private readonly int _maximumSnapshots;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrafficSnapshotStore(AppPaths paths, int maximumSnapshots = DefaultMaximumSnapshots)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        if (maximumSnapshots is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshots));
        }

        _maximumSnapshots = maximumSnapshots;
    }

    public async Task<IReadOnlyList<TrafficSnapshot>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount < 1)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Snapshots
                .OrderBy(snapshot => snapshot.CapturedAt)
                .TakeLast(maximumCount)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TrafficSnapshot>> GetRangeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toInclusive,
        CancellationToken cancellationToken = default)
    {
        if (toInclusive < fromInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(toInclusive));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Snapshots
                .Where(snapshot => snapshot.CapturedAt >= fromInclusive && snapshot.CapturedAt <= toInclusive)
                .OrderBy(snapshot => snapshot.CapturedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(TrafficSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = Normalize(snapshot);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            state.Snapshots.Add(normalized);
            if (state.Snapshots.Count > _maximumSnapshots)
            {
                state.Snapshots = state.Snapshots
                    .OrderBy(snapshotItem => snapshotItem.CapturedAt)
                    .TakeLast(_maximumSnapshots)
                    .ToList();
            }

            await JsonStore.WriteAtomicallyAsync(_paths.TrafficSnapshotsFile, state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await JsonStore.WriteAtomicallyAsync(
                _paths.TrafficSnapshotsFile,
                new TrafficSnapshotsState(),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<TrafficSnapshotsState> LoadStateAsync(CancellationToken cancellationToken) =>
        JsonStore.ReadOrDefaultAsync(_paths.TrafficSnapshotsFile, static () => new TrafficSnapshotsState(), cancellationToken);

    private static TrafficSnapshot Normalize(TrafficSnapshot source)
    {
        if (source.ProxyUploadBytes < 0 ||
            source.ProxyDownloadBytes < 0 ||
            source.DirectUploadBytes < 0 ||
            source.DirectDownloadBytes < 0 ||
            source.ActiveConnections < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Traffic values cannot be negative.");
        }

        return new TrafficSnapshot
        {
            CapturedAt = source.CapturedAt == default ? DateTimeOffset.UtcNow : source.CapturedAt,
            ProxyUploadBytes = source.ProxyUploadBytes,
            ProxyDownloadBytes = source.ProxyDownloadBytes,
            DirectUploadBytes = source.DirectUploadBytes,
            DirectDownloadBytes = source.DirectDownloadBytes,
            ActiveConnections = source.ActiveConnections,
        };
    }

    private static TrafficSnapshot Clone(TrafficSnapshot source) => new()
    {
        CapturedAt = source.CapturedAt,
        ProxyUploadBytes = source.ProxyUploadBytes,
        ProxyDownloadBytes = source.ProxyDownloadBytes,
        DirectUploadBytes = source.DirectUploadBytes,
        DirectDownloadBytes = source.DirectDownloadBytes,
        ActiveConnections = source.ActiveConnections,
    };
}
