using System.Xml.Linq;

namespace DefValidator.Core;

internal sealed record ModInfo(
    string PackageId,
    string Name,
    string RootPath,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> LoadFolders,
    IReadOnlyList<string> AssemblyPaths,
    int LoadOrder);

internal sealed record ModContext(
    ModInfo TargetMod,
    IReadOnlyList<ModInfo> ModsInLoadOrder,
    IReadOnlySet<string> ActivePackageIds,
    string GameDirectory,
    string ModsConfigPath);

internal static class ModContextBuilder
{
    public static ModContext? Build(ValidationOptions options, DiagnosticBag diagnostics)
    {
        if (!Directory.Exists(options.GameDirectory))
        {
            diagnostics.Add("CTX001", DiagnosticSeverity.Error, $"Game directory does not exist: {options.GameDirectory}", ValidationStage.Context, file: options.GameDirectory);
            return null;
        }

        if (!Directory.Exists(options.ModPath))
        {
            diagnostics.Add("CTX002", DiagnosticSeverity.Error, $"Mod directory does not exist: {options.ModPath}", ValidationStage.Context, file: options.ModPath);
            return null;
        }

        if (!File.Exists(options.ModsConfigPath))
        {
            diagnostics.Add("CTX003", DiagnosticSeverity.Error, $"ModsConfig.xml does not exist: {options.ModsConfigPath}", ValidationStage.Context, file: options.ModsConfigPath);
            return null;
        }

        var discoveredMods = DiscoverMods(options.GameDirectory, options.ModPath, diagnostics);
        if (discoveredMods.Count == 0)
        {
            diagnostics.Add("CTX004", DiagnosticSeverity.Error, "No mods could be discovered from --game-dir and target mod path.", ValidationStage.Context);
            return null;
        }

        var target = discoveredMods.Values.FirstOrDefault(static mod => Path.GetFullPath(mod.RootPath) == Path.GetFullPath(mod.RootPath));
        target = discoveredMods.Values.FirstOrDefault(mod => PathsEqual(mod.RootPath, options.ModPath));
        if (target is null)
        {
            diagnostics.Add("CTX005", DiagnosticSeverity.Error, $"Target mod was not discoverable: {options.ModPath}", ValidationStage.Context, file: options.ModPath);
            return null;
        }

        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld" };
        foreach (var id in ReadEnabledPackageIds(options.ModsConfigPath, diagnostics))
        {
            activeIds.Add(id);
        }

        foreach (var id in options.EnabledPackageIds)
        {
            activeIds.Add(id.Trim());
        }

        activeIds.Add(target.PackageId);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var activeId in activeIds.ToArray())
            {
                if (!discoveredMods.TryGetValue(activeId, out var mod))
                {
                    continue;
                }

                foreach (var dependency in mod.Dependencies)
                {
                    if (activeIds.Add(dependency))
                    {
                        changed = true;
                    }
                }
            }
        }

        var selectedMods = discoveredMods.Values
            .Where(mod => activeIds.Contains(mod.PackageId) || PathsEqual(mod.RootPath, options.ModPath))
            .ToDictionary(static mod => mod.PackageId, StringComparer.OrdinalIgnoreCase);

        foreach (var dependency in target.Dependencies)
        {
            if (!selectedMods.ContainsKey(dependency))
            {
                diagnostics.Add("CTX006", DiagnosticSeverity.Warning, $"Required dependency is not available: {dependency}", ValidationStage.Context, packageId: target.PackageId);
            }
        }

        if (!selectedMods.ContainsKey("ludeon.rimworld"))
        {
            diagnostics.Add("CTX007", DiagnosticSeverity.Error, "Core mod (packageId ludeon.rimworld) was not found under the game directory.", ValidationStage.Context, file: options.GameDirectory);
            return null;
        }

        var modConfigOrder = ReadEnabledPackageIds(options.ModsConfigPath, diagnostics).ToList();
        foreach (var id in options.EnabledPackageIds)
        {
            if (!modConfigOrder.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                modConfigOrder.Add(id);
            }
        }

        var loadOrder = TopologicalSort(selectedMods, modConfigOrder, target.PackageId);
        var orderedMods = loadOrder
            .Select((packageId, index) =>
            {
                var mod = selectedMods[packageId];
                return mod with { LoadOrder = index };
            })
            .ToList();

        var resolvedTarget = orderedMods.First(static mod => PathsEqual(mod.RootPath, mod.RootPath));
        resolvedTarget = orderedMods.First(mod => mod.PackageId.Equals(target.PackageId, StringComparison.OrdinalIgnoreCase));
        return new ModContext(resolvedTarget, orderedMods, activeIds, options.GameDirectory, options.ModsConfigPath);
    }

    private static Dictionary<string, ModInfo> DiscoverMods(string gameDirectory, string targetModPath, DiagnosticBag diagnostics)
    {
        var result = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateSearchRoots(gameDirectory, targetModPath))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var modDirectory in Directory.EnumerateDirectories(root))
            {
                TryAdd(result, modDirectory, diagnostics);
            }
        }

        TryAdd(result, targetModPath, diagnostics);
        return result;
    }

    private static IEnumerable<string> EnumerateSearchRoots(string gameDirectory, string targetModPath)
    {
        yield return Path.Combine(gameDirectory, "Data");
        yield return Path.Combine(gameDirectory, "Mods");

        var parent = Directory.GetParent(targetModPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            yield return parent;
        }
    }

    private static void TryAdd(IDictionary<string, ModInfo> mods, string modDirectory, DiagnosticBag diagnostics)
    {
        var aboutPath = Path.Combine(modDirectory, "About", "About.xml");
        if (!File.Exists(aboutPath))
        {
            return;
        }

        try
        {
            var document = XDocument.Load(aboutPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            var packageId = document.Descendants().FirstOrDefault(static element => element.Name.LocalName == "packageId")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(packageId))
            {
                packageId = Path.GetFileName(modDirectory);
            }

            var name = document.Descendants().FirstOrDefault(static element => element.Name.LocalName == "name")?.Value.Trim() ?? packageId;
            var dependencies = document.Descendants()
                .Where(static element => element.Parent?.Name.LocalName is "modDependencies" or "dependencies")
                .Select(static element => element.Value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var loadFolders = ReadLoadFolders(modDirectory);
            var assemblyPaths = loadFolders
                .Select(folder => Path.Combine(modDirectory, folder, "Assemblies"))
                .Where(Directory.Exists)
                .SelectMany(static path => Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            mods[packageId] = new ModInfo(packageId, name, modDirectory, dependencies, loadFolders, assemblyPaths, -1);
        }
        catch (Exception ex)
        {
            diagnostics.Add("XML002", DiagnosticSeverity.Error, $"Failed to read About.xml: {ex.Message}", ValidationStage.Context, file: aboutPath);
        }
    }

    private static IReadOnlyList<string> ReadEnabledPackageIds(string modsConfigPath, DiagnosticBag diagnostics)
    {
        try
        {
            var document = XDocument.Load(modsConfigPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            return document.Descendants()
                .Where(static element => element.Name.LocalName is "li" or "packageId")
                .Where(static element => element.Parent?.Name.LocalName is "activeMods" or "mods")
                .Select(static element => element.Value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }
        catch (Exception ex)
        {
            diagnostics.Add("XML003", DiagnosticSeverity.Error, $"Failed to read ModsConfig.xml: {ex.Message}", ValidationStage.Context, file: modsConfigPath);
            return [];
        }
    }

    private static IReadOnlyList<string> ReadLoadFolders(string modDirectory)
    {
        var loadFoldersPath = Path.Combine(modDirectory, "LoadFolders.xml");
        if (!File.Exists(loadFoldersPath))
        {
            return ["."];
        }

        try
        {
            var document = XDocument.Load(loadFoldersPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            var values = document.Descendants()
                .Where(static element => !element.HasElements)
                .Select(static element => element.Value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values.Count == 0 ? ["."] : values;
        }
        catch
        {
            return ["."];
        }
    }

    private static IReadOnlyList<string> TopologicalSort(
        IReadOnlyDictionary<string, ModInfo> mods,
        IReadOnlyList<string> preferredOrder,
        string targetPackageId)
    {
        var weights = preferredOrder
            .Select((packageId, index) => new { packageId, index })
            .ToDictionary(static item => item.packageId, static item => item.index, StringComparer.OrdinalIgnoreCase);

        var remaining = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Values)
        {
            remaining[mod.PackageId] = mod.Dependencies
                .Where(mods.ContainsKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var result = new List<string>();
        while (remaining.Count > 0)
        {
            var next = remaining
                .Where(static pair => pair.Value.Count == 0)
                .Select(static pair => pair.Key)
                .OrderBy(static id => id.Equals("ludeon.rimworld", StringComparison.OrdinalIgnoreCase) ? -1 : 0)
                .ThenBy(id => id.Equals(targetPackageId, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(id => weights.TryGetValue(id, out var weight) ? weight : int.MaxValue)
                .ThenBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (next is null)
            {
                next = remaining.Keys
                    .OrderBy(id => weights.TryGetValue(id, out var weight) ? weight : int.MaxValue)
                    .ThenBy(static id => id, StringComparer.OrdinalIgnoreCase)
                    .First();
            }

            remaining.Remove(next);
            foreach (var dependencySet in remaining.Values)
            {
                dependencySet.Remove(next);
            }

            result.Add(next);
        }

        result.RemoveAll(id => id.Equals(targetPackageId, StringComparison.OrdinalIgnoreCase));
        result.RemoveAll(static id => id.Equals("ludeon.rimworld", StringComparison.OrdinalIgnoreCase));
        result.Insert(0, "ludeon.rimworld");
        result.Add(targetPackageId);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
