using DefValidator.Core;

return await CliProgram.RunAsync(args);

internal static class CliProgram {
    public static async Task<int> RunAsync(string[] args) {
        var parseResult = CliParser.TryParse(args);
        if (!parseResult.Success) {
            await Console.Error.WriteLineAsync(parseResult.ErrorMessage);
            return 2;
        }

        var profileEnabled = ProfileOutput.IsEnabled();
        var run = profileEnabled
            ? await DefValidationEngine.ValidateWithProfileAsync(parseResult.Options!, CancellationToken.None)
            : new ValidationRun(await DefValidationEngine.ValidateAsync(parseResult.Options!, CancellationToken.None), []);
        foreach (var diagnostic in run.Result.Diagnostics) {
            Console.WriteLine(FormatText(diagnostic));
        }

        if (profileEnabled) {
            await ProfileOutput.WriteAsync(run.Timings);
        }

        return run.Result.GetExitCode();
    }

    private static string FormatText(Diagnostic diagnostic) {
        var prefix = diagnostic.File is null
            ? ""
            : $"{diagnostic.File}:{diagnostic.Line ?? 0}:{diagnostic.Column ?? 0}: ";

        var subject = string.Join(
            "/",
            new[] { diagnostic.PackageId, diagnostic.DefType, diagnostic.DefName }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

        return prefix +
               $"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message}" +
               (subject.Length > 0 ? $" [{subject}]" : string.Empty);
    }
}

internal static class ProfileOutput {
    public static bool IsEnabled() {
        var value = Environment.GetEnvironmentVariable("DEFVALIDATOR_PROFILE");
        return value is not null && value is not ("0" or "false" or "False");
    }

    public static async Task WriteAsync(IReadOnlyList<ValidationTiming> timings) {
        foreach (var timing in timings) {
            var suffix = timing.Count > 1 ? $" count={timing.Count}" : string.Empty;
            await Console.Error.WriteLineAsync($"profile: {timing.Name}={timing.Elapsed.TotalMilliseconds:F1}ms{suffix}");
        }
    }
}

internal sealed record CliParseResult(bool Success, string? ErrorMessage, ValidationOptions? Options);

internal static class CliParser {
    public static CliParseResult TryParse(IReadOnlyList<string> args) {
        try {
            if (args.Count == 0) {
                return Fail(
                    "Usage: defvalidator <mod-path> [--game-dir <path>]\nIf --game-dir is omitted, defvalidator tries the default Steam install path for the current user.");
            }

            var modPath = args[0];
            string? gameDir = null;

            for (var index = 1; index < args.Count; index++) {
                var arg = args[index];
                switch (arg) {
                    case "--game-dir":
                        gameDir = NextValue(args, ref index, arg);
                        break;
                    default:
                        return Fail($"Unknown argument: {arg}");
                }
            }

            gameDir ??= GameDirectoryLocator.TryFindDefault();
            if (string.IsNullOrWhiteSpace(gameDir)) {
                return Fail("Could not find RimWorld in the default Steam install path. Pass --game-dir <path>.");
            }

            return new CliParseResult(
                true,
                null,
                new ValidationOptions(modPath, gameDir));
        } catch (Exception ex) {
            return Fail(ex.Message);
        }
    }

    private static string NextValue(IReadOnlyList<string> args, ref int index, string option) {
        index++;
        if (index >= args.Count) {
            throw new InvalidOperationException($"Missing value for {option}.");
        }

        return args[index];
    }

    private static CliParseResult Fail(string message) => new(false, message, null);
}

internal static class GameDirectoryLocator {
    public static string? TryFindDefault() {
        foreach (var candidate in EnumerateCandidates()) {
            if (Directory.Exists(candidate)) {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates() {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) {
            yield return Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "RimWorld", "RimWorldMac.app");
            yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "RimWorld");
            yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "RimWorld");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86)) {
            yield return Path.Combine(programFilesX86, "Steam", "steamapps", "common", "RimWorld");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) {
            yield return Path.Combine(programFiles, "Steam", "steamapps", "common", "RimWorld");
        }
    }
}
