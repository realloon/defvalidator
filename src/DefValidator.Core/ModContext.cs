using System.Xml.Linq;

namespace DefValidator.Core;

internal sealed record ModInfo(
    string PackageId,
    string RootPath,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> LoadFolders,
    IReadOnlyList<string> AssemblyPaths);

internal sealed record ModContext(
    ModInfo TargetMod,
    IReadOnlyList<ModInfo> ModsInLoadOrder,
    IReadOnlySet<string> ActivePackageIds,
    string GameDirectory);

internal static class ModContextBuilder {
    private const string CorePackageId = "ludeon.rimworld";
    private const string WorkshopGameId = "294100";

    public static ModContext? Build(ValidationOptions options, DiagnosticBag diagnostics) {
        if (!Directory.Exists(options.GameDirectory)) {
            diagnostics.Add("CTX001", DiagnosticSeverity.Error,
                $"Game directory does not exist: {options.GameDirectory}", ValidationStage.Context,
                file: options.GameDirectory);
            return null;
        }

        if (!Directory.Exists(options.ModPath)) {
            diagnostics.Add("CTX002", DiagnosticSeverity.Error, $"Mod directory does not exist: {options.ModPath}",
                ValidationStage.Context, file: options.ModPath);
            return null;
        }

        var searchRoots = EnumerateSearchRoots(options.GameDirectory, options.ModPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (searchRoots.Count == 0) {
            diagnostics.Add("CTX004", DiagnosticSeverity.Error,
                "No mod search roots could be discovered from --game-dir and target mod path.",
                ValidationStage.Context);
            return null;
        }

        var catalog = new ModCatalog(searchRoots);
        var target = catalog.TryLoad(options.ModPath, diagnostics);
        if (target is null) {
            diagnostics.Add("CTX005", DiagnosticSeverity.Error, $"Target mod was not discoverable: {options.ModPath}",
                ValidationStage.Context, file: options.ModPath);
            return null;
        }

        var core = catalog.TryLoad(Path.Combine(options.GameDirectory, "Data", "Core"), diagnostics);
        if (core is null || !string.Equals(core.PackageId, CorePackageId, StringComparison.OrdinalIgnoreCase)) {
            diagnostics.Add("CTX007", DiagnosticSeverity.Error,
                "Core mod (packageId ludeon.rimworld) was not found under the game directory.", ValidationStage.Context,
                file: options.GameDirectory);
            return null;
        }

        var orderedMods = new List<ModInfo> { core };
        var activePackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CorePackageId, target.PackageId };

        foreach (var dependencyId in target.Dependencies) {
            var dependency = catalog.FindByPackageId(dependencyId);
            if (dependency is null) {
                diagnostics.Add("CTX006", DiagnosticSeverity.Warning,
                    $"Required dependency is not available: {dependencyId}", ValidationStage.Context,
                    packageId: target.PackageId);
                continue;
            }

            if (!activePackageIds.Add(dependency.PackageId)) {
                continue;
            }

            orderedMods.Add(dependency);
        }

        orderedMods.Add(target);
        return new ModContext(target, orderedMods, activePackageIds, options.GameDirectory);
    }

    private static IEnumerable<string> EnumerateSearchRoots(string gameDirectory, string targetModPath) {
        yield return Path.Combine(gameDirectory, "Data");
        yield return Path.Combine(gameDirectory, "Mods");

        var parent = Directory.GetParent(targetModPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent)) {
            yield return parent;
        }

        foreach (var workshopRoot in EnumerateWorkshopRoots(gameDirectory)) {
            yield return workshopRoot;
        }
    }

    private static IEnumerable<string> EnumerateWorkshopRoots(string gameDirectory) {
        var current = new DirectoryInfo(Path.GetFullPath(gameDirectory));
        while (current is not null) {
            if (current.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) {
                yield return Path.Combine(current.FullName, "workshop", "content", WorkshopGameId);
                yield break;
            }

            current = current.Parent;
        }
    }

    private static IReadOnlyList<string> ReadDependencies(XDocument document) {
        return document.Descendants()
            .Where(static element => element.Name.LocalName is "modDependencies" or "dependencies")
            .Elements()
            .Select(static element => {
                if (element.Name.LocalName == "packageId") {
                    return element.Value.Trim();
                }

                var nestedPackageId =
                    element.Elements().FirstOrDefault(static child => child.Name.LocalName == "packageId");
                if (nestedPackageId is not null) {
                    return nestedPackageId.Value.Trim();
                }

                if (!element.HasElements) {
                    return element.Value.Trim();
                }

                return string.Empty;
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ReadLoadFolders(string modDirectory) {
        var loadFoldersPath = Path.Combine(modDirectory, "LoadFolders.xml");
        if (!File.Exists(loadFoldersPath)) {
            return ["."];
        }

        try {
            var document = XDocument.Load(loadFoldersPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            var values = document.Descendants()
                .Where(static element => !element.HasElements)
                .Select(static element => element.Value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values.Count == 0 ? ["."] : values;
        } catch {
            return ["."];
        }
    }

    private sealed class ModCatalog(IReadOnlyList<string> searchRoots) {
        private readonly Dictionary<string, ModInfo> _modsByPackageId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModInfo?> _modsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _scannedRoots = new(StringComparer.OrdinalIgnoreCase);

        public ModInfo? TryLoad(string modDirectory, DiagnosticBag diagnostics) {
            var fullPath = Path.GetFullPath(modDirectory);
            if (_modsByPath.TryGetValue(fullPath, out var cached)) {
                return cached;
            }

            var mod = ReadModInfo(fullPath, diagnostics, reportErrors: true);
            _modsByPath[fullPath] = mod;
            if (mod is not null) {
                _modsByPackageId.TryAdd(mod.PackageId, mod);
            }

            return mod;
        }

        public ModInfo? FindByPackageId(string packageId) {
            if (_modsByPackageId.TryGetValue(packageId, out var cached)) {
                return cached;
            }

            foreach (var root in searchRoots) {
                if (!_scannedRoots.Add(root)) {
                    continue;
                }

                ScanRoot(root);
                if (_modsByPackageId.TryGetValue(packageId, out cached)) {
                    return cached;
                }
            }

            return null;
        }

        private void ScanRoot(string root) {
            foreach (var modDirectory in Directory.EnumerateDirectories(root)) {
                var fullPath = Path.GetFullPath(modDirectory);
                if (_modsByPath.ContainsKey(fullPath)) {
                    continue;
                }

                var mod = ReadModInfo(fullPath, diagnostics: null, reportErrors: false);
                _modsByPath[fullPath] = mod;
                if (mod is not null) {
                    _modsByPackageId.TryAdd(mod.PackageId, mod);
                }
            }
        }
    }

    private static ModInfo? ReadModInfo(string modDirectory, DiagnosticBag? diagnostics, bool reportErrors) {
        var aboutPath = Path.Combine(modDirectory, "About", "About.xml");
        if (!File.Exists(aboutPath)) {
            return null;
        }

        try {
            var document = XDocument.Load(aboutPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            var packageId = document.Descendants()
                .FirstOrDefault(static element => element.Name.LocalName == "packageId")
                ?.Value.Trim();
            if (string.IsNullOrWhiteSpace(packageId)) {
                packageId = Path.GetFileName(modDirectory);
            }

            var assemblyPaths = ReadLoadFolders(modDirectory)
                .Select(folder => Path.Combine(modDirectory, folder, "Assemblies"))
                .Where(Directory.Exists)
                .SelectMany(static path => Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ModInfo(packageId, modDirectory, ReadDependencies(document), ReadLoadFolders(modDirectory), assemblyPaths);
        } catch (Exception ex) {
            if (reportErrors) {
                diagnostics?.Add("XML002", DiagnosticSeverity.Error, $"Failed to read About.xml: {ex.Message}",
                    ValidationStage.Context, file: aboutPath);
            }

            return null;
        }
    }
}
