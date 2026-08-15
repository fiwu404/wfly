using WFly.Models;

namespace WFly.Services;

internal sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await JsonStore.ReadOrDefaultAsync(_paths.SettingsFile, () => new AppSettings(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await JsonStore.WriteAtomicallyAsync(_paths.SettingsFile, settings, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
