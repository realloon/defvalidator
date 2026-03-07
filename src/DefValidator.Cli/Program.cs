using DefValidator.Core;

return await CliProgram.RunAsync(args);

internal static class CliProgram {
    public static async Task<int> RunAsync(string[] args) {
        var parseResult = CliParser.TryParse(args);
        if (!parseResult.Success) {
            await Console.Error.WriteLineAsync(parseResult.ErrorMessage);
            return 2;
        }

        var result = await DefValidationEngine.ValidateAsync(parseResult.Options!, CancellationToken.None);
        foreach (var diagnostic in result.Diagnostics) {
            Console.WriteLine(FormatText(diagnostic));
        }

        return result.GetExitCode();
    }

    private static string FormatText(Diagnostic diagnostic) {
        var location = diagnostic.File is null
            ? ""
            : $" {diagnostic.File}:{diagnostic.Line ?? 0}:{diagnostic.Column ?? 0}";

        var subject = string.Join(
            "/",
            new[] { diagnostic.PackageId, diagnostic.DefType, diagnostic.DefName }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

        return $"{diagnostic.Severity.ToString().ToUpperInvariant()} {diagnostic.Code} [{diagnostic.Stage}]" +
               (subject.Length > 0 ? $" {subject}" : string.Empty) +
               location +
               $" {diagnostic.Message}";
    }
}

internal sealed record CliParseResult(bool Success, string? ErrorMessage, ValidationOptions? Options);

internal static class CliParser {
    public static CliParseResult TryParse(IReadOnlyList<string> args) {
        try {
            if (args.Count == 0) {
                return Fail(
                    "Usage: defvalidator <mod-path> [--game-dir <path>]\nMissing --game-dir can be filled from .env via DEFVALIDATOR_GAME_DIR.");
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

            var dotEnvPath = Path.Combine(Environment.CurrentDirectory, ".env");
            gameDir ??= DotEnvFile.ReadValue(dotEnvPath, "DEFVALIDATOR_GAME_DIR");

            if (string.IsNullOrWhiteSpace(gameDir)) {
                return Fail("Missing required option --game-dir. You can also set DEFVALIDATOR_GAME_DIR in .env.");
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

internal static class DotEnvFile {
    public static string? ReadValue(string path, string key) {
        if (!File.Exists(path)) {
            return null;
        }

        foreach (var rawLine in File.ReadLines(path)) {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0) {
                continue;
            }

            if (!line[..separatorIndex].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var value = line[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2 && ((value.StartsWith('"') && value.EndsWith('"')) ||
                                      (value.StartsWith("'") && value.EndsWith("'")))) {
                return value[1..^1];
            }

            return value;
        }

        return null;
    }
}
