using WFly.Services;
using WFly.UI;

namespace WFly;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var paths = new AppPaths();
        paths.EnsureDirectories();

        using var httpClient = HttpClientFactory.Create();
        var installedCoreStore = new InstalledCoreStore(paths);
        var settingsStore = new SettingsStore(paths);
        var catalogService = new CoreCatalogService(httpClient);
        var installer = new CoreInstaller(httpClient, paths, installedCoreStore);
        using var processService = new CoreProcessService();

        Application.Run(new MainForm(
            paths,
            installedCoreStore,
            settingsStore,
            catalogService,
            installer,
            processService));
    }
}
