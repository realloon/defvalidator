using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using DefValidator.Core;

var runner = new TestRunner();
await runner.RunAsync();

internal sealed class TestRunner {
    private readonly List<(string Name, Func<Task> Test)> _tests = [
        (nameof(MissingArgs_Returns2), MissingArgs_Returns2),
        (nameof(UnknownArgument_Returns2), UnknownArgument_Returns2),
        (nameof(AutoDetectedGameDir_ValidatesModPath), AutoDetectedGameDir_ValidatesModPath),
        (nameof(ModsConfig_IsNotSupported_Returns2), ModsConfig_IsNotSupported_Returns2),
        (nameof(MissingGameDirectory_UsesFileFirstFormat), MissingGameDirectory_UsesFileFirstFormat),
        (nameof(ProfileEnv_WritesTimingsToStdErr), ProfileEnv_WritesTimingsToStdErr),
        (nameof(InheritanceResolver_PreservesSourceAnnotations), InheritanceResolver_PreservesSourceAnnotations)
    ];

    public async Task RunAsync() {
        var failures = new List<string>();
        foreach (var (name, test) in _tests) {
            try {
                await test();
                Console.WriteLine($"PASS {name}");
            } catch (Exception ex) {
                failures.Add($"FAIL {name}: {ex.Message}");
                await Console.Error.WriteLineAsync($"FAIL {name}: {ex}");
            }
        }

        if (failures.Count > 0) {
            throw new Exception(string.Join(Environment.NewLine, failures));
        }
    }

    private static async Task MissingArgs_Returns2() {
        var result = await RunCliAsync([]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.StdErr, "Usage: defvalidator <mod-path> [--game-dir <path>]");
    }

    private static async Task UnknownArgument_Returns2() {
        var result = await RunCliAsync(["some-mod", "--wat"]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.StdErr, "Unknown argument: --wat");
    }

    private static async Task AutoDetectedGameDir_ValidatesModPath() {
        var result = await RunCliAsync(["/defvalidator/missing-mod-path"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.StdOut, "/defvalidator/missing-mod-path:0:0: error CTX002: Mod directory does not exist: /defvalidator/missing-mod-path");
    }

    private static async Task ModsConfig_IsNotSupported_Returns2() {
        var result = await RunCliAsync(["some-mod", "--mods-config", "ModsConfig.xml"]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.StdErr, "Unknown argument: --mods-config");
    }

    private static async Task MissingGameDirectory_UsesFileFirstFormat() {
        var result = await RunCliAsync(["some-mod", "--game-dir", "/defvalidator/missing-game-dir"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.StdOut, "/defvalidator/missing-game-dir:0:0: error CTX001: Game directory does not exist: /defvalidator/missing-game-dir");
    }

    private static async Task ProfileEnv_WritesTimingsToStdErr() {
        var result = await RunCliAsync(["some-mod", "--game-dir", "/defvalidator/missing-game-dir"],
            new Dictionary<string, string?> { ["DEFVALIDATOR_PROFILE"] = "1" });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.StdErr, "profile: build_context=");
        Assert.Contains(result.StdErr, "profile: total=");
    }

    private static Task InheritanceResolver_PreservesSourceAnnotations() {
        var coreAssembly = typeof(DefValidationEngine).Assembly;
        var diagnostics = CreateInternal(coreAssembly, "DefValidator.Core.DiagnosticBag");
        var sourceInfoType = coreAssembly.GetType("DefValidator.Core.SourceInfo", throwOnError: true)!;
        var resolve = coreAssembly
            .GetType("DefValidator.Core.InheritanceResolver", throwOnError: true)!
            .GetMethod("Resolve", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

        var rootDef = new XElement("ThingDef", new XElement("defName", "ExampleDef"), new XElement("x", "hello"));
        AddSourceInfo(rootDef, sourceInfoType, "/tmp/example.xml", 3, 4, "example.mod");
        AddSourceInfo(rootDef.Element("x")!, sourceInfoType, "/tmp/example.xml", 4, 6, "example.mod");

        var result = (XDocument)resolve.Invoke(null, [new XDocument(new XElement("Defs", rootDef)), diagnostics])!;
        var resolvedDef = result.Root!.Element("ThingDef")!;
        var resolvedChild = resolvedDef.Element("x")!;

        var resolvedDefSource = GetAnnotation(resolvedDef, sourceInfoType);
        var resolvedChildSource = GetAnnotation(resolvedChild, sourceInfoType);
        Assert.NotNull(resolvedDefSource);
        Assert.NotNull(resolvedChildSource);
        Assert.Equal("example.mod", sourceInfoType.GetProperty("PackageId")!.GetValue(resolvedDefSource));
        Assert.Equal("example.mod", sourceInfoType.GetProperty("PackageId")!.GetValue(resolvedChildSource));
        Assert.Equal("/tmp/example.xml", sourceInfoType.GetProperty("File")!.GetValue(resolvedChildSource));
        return Task.CompletedTask;
    }

    private static object CreateInternal(Assembly assembly, string typeName, params object?[] args) {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return Activator.CreateInstance(type,
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   args: args,
                   culture: null)
               ?? throw new InvalidOperationException($"Failed to create {typeName}.");
    }

    private static void AddSourceInfo(XElement element, Type sourceInfoType, string file, int line, int column, string packageId) {
        var annotation = Activator.CreateInstance(sourceInfoType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [file, line, column, packageId],
            culture: null) ?? throw new InvalidOperationException("Failed to create SourceInfo.");
        element.AddAnnotation(annotation);
    }

    private static object? GetAnnotation(XObject node, Type annotationType) {
        var method = typeof(XObject).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(candidate => candidate.Name == nameof(XObject.Annotation)
                                 && candidate.IsGenericMethodDefinition
                                 && candidate.GetParameters().Length == 0)
            .MakeGenericMethod(annotationType);
        return method.Invoke(node, []);
    }

    private static async Task<CliResult> RunCliAsync(string[] args, IReadOnlyDictionary<string, string?>? environment = null) {
        var repoRoot = FindRepoRoot();
        var cliDll = FindCliDll(repoRoot);
        var startInfo = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot
        };

        if (environment is not null) {
            foreach (var pair in environment) {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        startInfo.ArgumentList.Add(cliDll);
        foreach (var arg in args) {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Failed to start CLI process.");
        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, stdOut, stdErr);
    }

    private static string FindCliDll(string repoRoot) {
        var candidates = new[] {
            Path.Combine(repoRoot, "src", "DefValidator.Cli", "bin", "Release", "net10.0", "defvalidator.dll"),
            Path.Combine(repoRoot, "src", "DefValidator.Cli", "bin", "Debug", "net10.0", "defvalidator.dll")
        };

        var cliDll = candidates.FirstOrDefault(File.Exists);
        if (cliDll is null) {
            throw new InvalidOperationException("Could not locate CLI output under src/DefValidator.Cli/bin.");
        }

        return cliDll;
    }

    private static string FindRepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "DefValidator.slnx"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);
}

internal static class Assert {
    public static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
        }
    }

    public static void Contains(string value, string expectedSubstring) {
        if (!value.Contains(expectedSubstring, StringComparison.Ordinal)) {
            throw new InvalidOperationException($"Expected substring '{expectedSubstring}' in: {value}");
        }
    }

    public static void NotNull(object? value) {
        if (value is null) {
            throw new InvalidOperationException("Expected non-null value.");
        }
    }
}
