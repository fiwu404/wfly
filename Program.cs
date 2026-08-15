using WFly.Services;
using WFly.UI;

namespace WFly;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var resumeTunAfterElevation = Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => string.Equals(argument, "--resume-tun", StringComparison.Ordinal));

        var paths = new AppPaths();
        paths.EnsureDirectories();

        using var httpClient = HttpClientFactory.Create();
        var installedCoreStore = new InstalledCoreStore(paths);
        var settingsStore = new SettingsStore(paths);
        var nodeGroupStore = new NodeGroupStore(paths);
        var proxyNodeStore = new ProxyNodeStore(paths, nodeGroupStore);
        var ruleSetStore = new RuleSetStore(paths);
        var catalogService = new CoreCatalogService(httpClient);
        var installer = new CoreInstaller(httpClient, paths, installedCoreStore);
        var subscriptionProfileService = new SubscriptionProfileService(httpClient, paths);
        var profileGenerationService = new ProfileGenerationService(paths);
        var logStore = new InMemoryLogStore();
        var networkDiagnosticsService = new NetworkDiagnosticsService();
        var siteLatencyTestService = new SiteLatencyTestService();
        var clashApiClient = new ClashApiClient();
        var systemProxyService = new WindowsSystemProxyService();
        using var processService = new CoreProcessService();

        Application.Run(new DashboardForm(
            paths,
            installedCoreStore,
            settingsStore,
            nodeGroupStore,
            proxyNodeStore,
            ruleSetStore,
            catalogService,
            installer,
            subscriptionProfileService,
            profileGenerationService,
            processService,
            logStore,
            networkDiagnosticsService,
            siteLatencyTestService,
            clashApiClient,
            systemProxyService,
            resumeTunAfterElevation));
    }
}
