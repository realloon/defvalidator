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

        return result.GetExitCode(parseResult.Options!.Strict);
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
                    "Usage: defvalidator <mod-path> [--game-dir <path>] [--mods-config <path>] [--strict]\nMissing --game-dir/--mods-config can be filled from .env via DEFVALIDATOR_GAME_DIR and DEFVALIDATOR_MODS_CONFIG.");
            }

            var modPath = args[0];
            string? gameDir = null;
            string? modsConfig = null;
            var strict = false;

            for (var index = 1; index < args.Count; index++) {
                var arg = args[index];
                switch (arg) {
                    case "--game-dir":
                        gameDir = NextValue(args, ref index, arg);
                        break;
                    case "--mods-config":
                        modsConfig = NextValue(args, ref index, arg);
                        break;
                    case "--strict":
                        strict = true;
                        break;
                    default:
                        return Fail($"Unknown argument: {arg}");
                }
            }

            var dotEnv = DotEnvFile.Load(Path.Combine(Environment.CurrentDirectory, ".env"));
            gameDir ??= dotEnv.GetValueOrDefault("DEFVALIDATOR_GAME_DIR");
            modsConfig ??= dotEnv.GetValueOrDefault("DEFVALIDATOR_MODS_CONFIG");

            if (string.IsNullOrWhiteSpace(gameDir)) {
                return Fail("Missing required option --game-dir. You can also set DEFVALIDATOR_GAME_DIR in .env.");
            }

            if (string.IsNullOrWhiteSpace(modsConfig)) {
                return Fail(
                    "Missing required option --mods-config. You can also set DEFVALIDATOR_MODS_CONFIG in .env.");
            }

            return new CliParseResult(
                true,
                null,
                new ValidationOptions(modPath, gameDir, modsConfig, strict));
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
    public static IReadOnlyDictionary<string, string> Load(string path) {
        if (!File.Exists(path)) {
            return Empty;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path)) {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0) {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2 && ((value.StartsWith('"') && value.EndsWith('"')) ||
                                      (value.StartsWith("'") && value.EndsWith("'")))) {
                value = value[1..^1];
            }

            if (key.Length > 0) {
                values[key] = value;
            }
        }

        return values;
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
}