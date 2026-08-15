using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WFly.Services;

/// <summary>
/// Centralizes every writable path used by WFly.
///
/// The application is intentionally portable: when the published executable is
/// placed in <c>release</c>, data is stored in its sibling <c>data</c> folder.
/// Development builds locate the project file first so they use the same
/// workspace-level data folder instead of a nested bin directory.
/// </summary>
internal sealed class AppPaths
{
    private const string ProductDirectoryName = "WFly";

    public AppPaths()
    {
        RootDirectory = ResolveDataDirectory();
        CoresDirectory = Path.Combine(RootDirectory, "cores");
        ProfilesDirectory = Path.Combine(RootDirectory, "profiles");
        RulesDirectory = Path.Combine(RootDirectory, "rules");
        ExportsDirectory = Path.Combine(RootDirectory, "exports");
        StateDirectory = Path.Combine(RootDirectory, "state");
        TempDirectory = Path.Combine(RootDirectory, "temp");

        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        InstalledCoresFile = Path.Combine(StateDirectory, "installed-cores.json");
        NodeGroupsFile = Path.Combine(StateDirectory, "node-groups.json");
        ProxyNodesFile = Path.Combine(StateDirectory, "proxy-nodes.json");
        RuleSetsFile = Path.Combine(StateDirectory, "rule-sets.json");
        TrafficSnapshotsFile = Path.Combine(StateDirectory, "traffic-snapshots.json");

        LegacyRootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductDirectoryName);
    }

    /// <summary>Portable application data root (normally &lt;workspace&gt;/data).</summary>
    public string RootDirectory { get; }
    public string CoresDirectory { get; }
    public string ProfilesDirectory { get; }
    public string RulesDirectory { get; }
    public string ExportsDirectory { get; }
    public string StateDirectory { get; }
    public string TempDirectory { get; }
    public string SettingsFile { get; }
    public string InstalledCoresFile { get; }
    public string NodeGroupsFile { get; }
    public string ProxyNodesFile { get; }
    public string RuleSetsFile { get; }
    public string TrafficSnapshotsFile { get; }

    /// <summary>
    /// Previous releases stored data here. It is exposed for diagnostics only;
    /// new data is never created in this location.
    /// </summary>
    public string LegacyRootDirectory { get; }

    /// <summary>
    /// True after a legacy data folder was found and at least one item was
    /// copied or moved to the portable data directory during this process.
    /// </summary>
    public bool LegacyDataMigrated { get; private set; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        MigrateLegacyDataIfNeeded();

        Directory.CreateDirectory(CoresDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(RulesDirectory);
        Directory.CreateDirectory(ExportsDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(TempDirectory);

        RewriteMigratedPathReferences();
    }

    private static string ResolveDataDirectory()
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var projectDirectory = FindProjectDirectory(baseDirectory);
        if (projectDirectory is not null)
        {
            var workspaceDirectory = Directory.GetParent(projectDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(workspaceDirectory))
            {
                return Path.GetFullPath(Path.Combine(workspaceDirectory, "data"));
            }
        }

        // A published WFly.exe lives in <workspace>/release, making this
        // resolve to <workspace>/data. It also keeps a standalone copy portable.
        return Path.GetFullPath(Path.Combine(baseDirectory, "..", "data"));
    }

    private static string? FindProjectDirectory(string baseDirectory)
    {
        var current = new DirectoryInfo(baseDirectory);
        for (var depth = 0; current is not null && depth < 12; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "WFly.csproj")))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private void MigrateLegacyDataIfNeeded()
    {
        if (!Directory.Exists(LegacyRootDirectory) || PathsEqual(LegacyRootDirectory, RootDirectory))
        {
            return;
        }

        try
        {
            foreach (var sourcePath in Directory.EnumerateFileSystemEntries(LegacyRootDirectory))
            {
                var destinationPath = Path.Combine(RootDirectory, Path.GetFileName(sourcePath));
                if (Directory.Exists(sourcePath))
                {
                    LegacyDataMigrated |= MigrateDirectory(sourcePath, destinationPath);
                }
                else if (File.Exists(sourcePath))
                {
                    LegacyDataMigrated |= MigrateFile(sourcePath, destinationPath);
                }
            }
        }
        catch (IOException)
        {
            // Migration is best-effort. A read-only or locked legacy folder
            // must not prevent a portable WFly instance from starting.
        }
        catch (UnauthorizedAccessException)
        {
            // See the comment above.
        }
    }

    private static bool MigrateDirectory(string sourcePath, string destinationPath)
    {
        if (IsReparsePoint(sourcePath))
        {
            return false;
        }

        if (!Directory.Exists(destinationPath) && !File.Exists(destinationPath))
        {
            if (CanMove(sourcePath, destinationPath))
            {
                try
                {
                    Directory.Move(sourcePath, destinationPath);
                    return true;
                }
                catch (IOException)
                {
                    // Fall back to a safe copy below.
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }

            return CopyDirectoryToNewDestination(sourcePath, destinationPath);
        }

        if (!Directory.Exists(destinationPath))
        {
            return false;
        }

        var migrated = false;
        try
        {
            foreach (var childSourcePath in Directory.EnumerateFileSystemEntries(sourcePath))
            {
                var childDestinationPath = Path.Combine(destinationPath, Path.GetFileName(childSourcePath));
                if (Directory.Exists(childSourcePath))
                {
                    migrated |= MigrateDirectory(childSourcePath, childDestinationPath);
                }
                else if (File.Exists(childSourcePath))
                {
                    migrated |= MigrateFile(childSourcePath, childDestinationPath);
                }
            }
        }
        catch (IOException)
        {
            return migrated;
        }
        catch (UnauthorizedAccessException)
        {
            return migrated;
        }

        return migrated;
    }

    private static bool MigrateFile(string sourcePath, string destinationPath)
    {
        if (IsReparsePoint(sourcePath) || File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            // Never overwrite portable data. The legacy copy remains available
            // for manual recovery when names collide.
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (CanMove(sourcePath, destinationPath))
            {
                File.Move(sourcePath, destinationPath);
                return true;
            }

            // Cross-volume moves are implemented as a copy. Keeping the old
            // source as a backup is deliberate: it prevents data loss if a
            // machine is interrupted while a user verifies the migration.
            var temporaryPath = Path.Combine(
                Path.GetDirectoryName(destinationPath)!,
                $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.migration");
            try
            {
                File.Copy(sourcePath, temporaryPath, overwrite: false);
                File.Move(temporaryPath, destinationPath, overwrite: false);
                return true;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CopyDirectoryToNewDestination(string sourcePath, string destinationPath)
    {
        var destinationParent = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationParent))
        {
            return false;
        }

        var stagingPath = Path.Combine(destinationParent, $".migration-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(sourcePath, stagingPath);
            Directory.Move(stagingPath, destinationPath);
            // Unlike same-volume Directory.Move, a cross-volume copy retains a
            // legacy backup until the user has confirmed the new portable data.
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                try
                {
                    Directory.Delete(stagingPath, recursive: true);
                }
                catch (IOException)
                {
                    // A failed cleanup is harmless and remains within data/.
                }
                catch (UnauthorizedAccessException)
                {
                    // A failed cleanup is harmless and remains within data/.
                }
            }
        }
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        if (IsReparsePoint(sourcePath))
        {
            throw new IOException("Legacy data contains a reparse-point directory.");
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var childSourcePath in Directory.EnumerateFileSystemEntries(sourcePath))
        {
            var childDestinationPath = Path.Combine(destinationPath, Path.GetFileName(childSourcePath));
            if (Directory.Exists(childSourcePath))
            {
                CopyDirectory(childSourcePath, childDestinationPath);
            }
            else if (File.Exists(childSourcePath))
            {
                if (IsReparsePoint(childSourcePath))
                {
                    throw new IOException("Legacy data contains a reparse-point file.");
                }

                File.Copy(childSourcePath, childDestinationPath, overwrite: false);
            }
        }
    }

    private void RewriteMigratedPathReferences()
    {
        RewriteJsonPathProperties(SettingsFile, "ConfigPath");
        RewriteJsonPathProperties(InstalledCoresFile, "ExecutablePath");
    }

    private void RewriteJsonPathProperties(string filePath, string propertyName)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(filePath));
            if (root is null || !RewriteJsonPathProperties(root, propertyName))
            {
                return;
            }

            var temporaryPath = Path.Combine(
                Path.GetDirectoryName(filePath)!,
                $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.migration");
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    root.ToJsonString(JsonStore.IndentedOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporaryPath, filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (IOException)
        {
            // Existing state remains usable; the stores can still load it.
        }
        catch (UnauthorizedAccessException)
        {
            // Existing state remains usable; the stores can still load it.
        }
        catch (JsonException)
        {
            // Do not overwrite malformed user state during a migration.
        }
    }

    private bool RewriteJsonPathProperties(JsonNode node, string propertyName)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToArray())
                {
                    if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase) &&
                        property.Value is JsonValue jsonValue &&
                        jsonValue.TryGetValue<string>(out var storedPath) &&
                        TryMapLegacyPath(storedPath, out var migratedPath))
                    {
                        jsonObject[property.Key] = migratedPath;
                        changed = true;
                    }

                    if (string.Equals(property.Key, "NativeConfigPaths", StringComparison.OrdinalIgnoreCase) &&
                        property.Value is JsonObject nativeConfigPaths)
                    {
                        foreach (var nativePath in nativeConfigPaths.ToArray())
                        {
                            if (nativePath.Value is JsonValue nativePathValue &&
                                nativePathValue.TryGetValue<string>(out var storedNativePath) &&
                                TryMapLegacyPath(storedNativePath, out var migratedNativePath))
                            {
                                nativeConfigPaths[nativePath.Key] = migratedNativePath;
                                changed = true;
                            }
                        }
                    }

                    if (property.Value is not null)
                    {
                        changed |= RewriteJsonPathProperties(property.Value, propertyName);
                    }
                }

                break;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    if (item is not null)
                    {
                        changed |= RewriteJsonPathProperties(item, propertyName);
                    }
                }

                break;
        }

        return changed;
    }

    private bool TryMapLegacyPath(string? storedPath, out string migratedPath)
    {
        migratedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return false;
        }

        try
        {
            var fullLegacyRoot = Path.GetFullPath(LegacyRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullStoredPath = Path.GetFullPath(storedPath);
            var relativePath = Path.GetRelativePath(fullLegacyRoot, fullStoredPath);
            if (relativePath is "." or ".." ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                return false;
            }

            migratedPath = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool CanMove(string sourcePath, string destinationPath) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(sourcePath)),
            Path.GetPathRoot(Path.GetFullPath(destinationPath)),
            StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
