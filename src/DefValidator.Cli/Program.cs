using System.Text.Json;
using DefValidator.Core;

return await CliProgram.RunAsync(args);

internal static class CliProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parseResult = CliParser.TryParse(args);
        if (!parseResult.Success)
        {
            Console.Error.WriteLine(parseResult.ErrorMessage);
            return 2;
        }

        var engine = new DefValidationEngine();
        var result = await engine.ValidateAsync(parseResult.Options!, CancellationToken.None);

        if (parseResult.Format == OutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, ValidationResultJsonContext.Default.ValidationResult));
        }
        else
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                Console.WriteLine(FormatText(diagnostic));
            }
        }

        return result.GetExitCode(parseResult.Options!.Strict);
    }

    private static string FormatText(Diagnostic diagnostic)
    {
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

internal enum OutputFormat
{
    Text,
    Json
}

internal sealed record CliParseResult(bool Success, string? ErrorMessage, ValidationOptions? Options, OutputFormat Format);

internal static class CliParser
{
    public static CliParseResult TryParse(IReadOnlyList<string> args)
    {
        try
        {
            if (args.Count == 0 || !string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Usage: defvalidator validate <mod-path> --game-dir <path> --mods-config <path> [--format text|json] [--strict] [--enabled-package-id <id> ...] [--no-patches]");
            }

            if (args.Count < 2)
            {
                return Fail("Missing <mod-path>.");
            }

            var modPath = args[1];
            string? gameDir = null;
            string? modsConfig = null;
            var enabledPackageIds = new List<string>();
            var strict = false;
            var applyPatches = true;
            var format = OutputFormat.Text;

            for (var index = 2; index < args.Count; index++)
            {
                var arg = args[index];
                switch (arg)
                {
                    case "--game-dir":
                        gameDir = NextValue(args, ref index, arg);
                        break;
                    case "--mods-config":
                        modsConfig = NextValue(args, ref index, arg);
                        break;
                    case "--enabled-package-id":
                        enabledPackageIds.Add(NextValue(args, ref index, arg));
                        break;
                    case "--format":
                        var value = NextValue(args, ref index, arg);
                        format = value.ToLowerInvariant() switch
                        {
                            "text" => OutputFormat.Text,
                            "json" => OutputFormat.Json,
                            _ => throw new InvalidOperationException($"Unsupported --format value: {value}")
                        };
                        break;
                    case "--strict":
                        strict = true;
                        break;
                    case "--no-patches":
                        applyPatches = false;
                        break;
                    default:
                        return Fail($"Unknown argument: {arg}");
                }
            }

            if (string.IsNullOrWhiteSpace(gameDir))
            {
                return Fail("Missing required option --game-dir.");
            }

            if (string.IsNullOrWhiteSpace(modsConfig))
            {
                return Fail("Missing required option --mods-config.");
            }

            return new CliParseResult(
                true,
                null,
                new ValidationOptions(modPath, gameDir, modsConfig, enabledPackageIds, strict, applyPatches),
                format);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static string NextValue(IReadOnlyList<string> args, ref int index, string option)
    {
        index++;
        if (index >= args.Count)
        {
            throw new InvalidOperationException($"Missing value for {option}.");
        }

        return args[index];
    }

    private static CliParseResult Fail(string message) => new(false, message, null, OutputFormat.Text);
}
