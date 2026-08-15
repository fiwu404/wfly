namespace WFly.Services;

internal sealed class AppPaths
{
    public AppPaths()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WFly");
        CoresDirectory = Path.Combine(RootDirectory, "cores");
        StateDirectory = Path.Combine(RootDirectory, "state");
        TempDirectory = Path.Combine(RootDirectory, "temp");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        InstalledCoresFile = Path.Combine(StateDirectory, "installed-cores.json");
    }

    public string RootDirectory { get; }
    public string CoresDirectory { get; }
    public string StateDirectory { get; }
    public string TempDirectory { get; }
    public string SettingsFile { get; }
    public string InstalledCoresFile { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(CoresDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
