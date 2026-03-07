using System.Diagnostics;

var runner = new TestRunner();
await runner.RunAsync();

internal sealed class TestRunner {
    private readonly List<(string Name, Func<Task> Test)> _tests = [
        (nameof(MissingArgs_Returns2), MissingArgs_Returns2),
        (nameof(UnknownArgument_Returns2), UnknownArgument_Returns2),
        (nameof(AutoDetectedGameDir_ValidatesModPath), AutoDetectedGameDir_ValidatesModPath),
        (nameof(ModsConfig_IsNotSupported_Returns2), ModsConfig_IsNotSupported_Returns2),
        (nameof(MissingGameDirectory_UsesFileFirstFormat), MissingGameDirectory_UsesFileFirstFormat)
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

    private static async Task<CliResult> RunCliAsync(string[] args) {
        var repoRoot = FindRepoRoot();
        var cliDll = Path.Combine(repoRoot, "src", "DefValidator.Cli", "bin", "Debug", "net10.0", "defvalidator.dll");
        var startInfo = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot
        };

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
}
