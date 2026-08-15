using WFly.Models;

namespace WFly.Services;

internal sealed class InstalledCoreStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InstalledCoreStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<InstalledCore>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Cores
                .OrderByDescending(core => core.InstalledAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InstalledCore?> GetLatestAsync(string coreId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coreId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            return state.Cores
                .Where(core => string.Equals(core.Id, coreId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(core => core.InstalledAt)
                .Select(Clone)
                .FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(InstalledCore installedCore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installedCore);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            state.Cores.RemoveAll(core =>
                string.Equals(core.Id, installedCore.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(core.Version, installedCore.Version, StringComparison.OrdinalIgnoreCase));
            state.Cores.Add(Clone(installedCore));
            await JsonStore.WriteAtomicallyAsync(_paths.InstalledCoresFile, state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<InstalledCoresState> LoadStateAsync(CancellationToken cancellationToken) =>
        JsonStore.ReadOrDefaultAsync(_paths.InstalledCoresFile, () => new InstalledCoresState(), cancellationToken);

    private static InstalledCore Clone(InstalledCore core) => new()
    {
        Id = core.Id,
        Version = core.Version,
        ExecutablePath = core.ExecutablePath,
        ArchiveSha256 = core.ArchiveSha256,
        InstalledAt = core.InstalledAt,
    };
}
