using System.Diagnostics;
using DefValidator.Core;

var runner = new TestRunner();
await runner.RunAsync();

internal sealed class TestRunner
{
    private readonly List<(string Name, Func<Task> Test)> _tests;

    public TestRunner()
    {
        _tests =
        [
            (nameof(MissingArgs_Returns2), MissingArgs_Returns2),
            (nameof(InvalidXml_ProducesXml001), InvalidXml_ProducesXml001),
            (nameof(MissingDefName_ProducesRule001), MissingDefName_ProducesRule001),
            (nameof(DuplicateDefName_ProducesXref001), DuplicateDefName_ProducesXref001),
            (nameof(BadParentName_ProducesInherit001), BadParentName_ProducesInherit001),
            (nameof(UnknownClass_ProducesType001), UnknownClass_ProducesType001),
            (nameof(FieldTypeMismatch_ProducesType005), FieldTypeMismatch_ProducesType005),
            (nameof(CustomAssemblyAndCrossModReference_Succeeds), CustomAssemblyAndCrossModReference_Succeeds),
            (nameof(FiltersOutExternalContextNoise), FiltersOutExternalContextNoise),
            (nameof(DotEnv_FillsGameDirAndModsConfig), DotEnv_FillsGameDirAndModsConfig)
        ];
    }

    public async Task RunAsync()
    {
        var failures = new List<string>();
        foreach (var (name, test) in _tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"FAIL {name}: {ex.Message}");
                Console.Error.WriteLine($"FAIL {name}: {ex}");
            }
        }

        if (failures.Count > 0)
        {
            throw new Exception(string.Join(Environment.NewLine, failures));
        }
    }

    private async Task MissingArgs_Returns2()
    {
        var result = await RunCliAsync([]);
        Assert.Equal(2, result.ExitCode);
    }

    private async Task InvalidXml_ProducesXml001()
    {
        using var fixture = TestFixture.Create();
        fixture.WriteTargetDef("Bad.xml", "<Defs><ThingDef><defName>Broken</defName>");

        var result = await ValidateAsync(fixture);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "XML001");
    }

    private async Task MissingDefName_ProducesRule001()
    {
        using var fixture = TestFixture.Create();
        fixture.WriteTargetDef("Defs.xml", "<Defs><ThingDef><statBase>1</statBase></ThingDef></Defs>");

        var cli = await RunCliAsync(fixture.CreateCliArgs());
        Assert.Equal(1, cli.ExitCode);

        var normalized = fixture.Normalize(cli.StdOut);
        Assert.Contains(normalized, "RULE001");
        Assert.Contains(normalized, "Missing defName.");
    }

    private async Task DuplicateDefName_ProducesXref001()
    {
        using var fixture = TestFixture.Create();
        fixture.WriteTargetDef("Defs.xml", "<Defs><ThingDef><defName>Dup</defName><statBase>1</statBase></ThingDef><ThingDef><defName>Dup</defName><statBase>2</statBase></ThingDef></Defs>");

        var result = await ValidateAsync(fixture);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "XREF001");
    }

    private async Task BadParentName_ProducesInherit001()
    {
        using var fixture = TestFixture.Create();
        fixture.WriteTargetDef("Defs.xml", "<Defs><ThingDef ParentName=\"MissingBase\"><defName>Child</defName><statBase>1</statBase></ThingDef></Defs>");

        var result = await ValidateAsync(fixture);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "INHERIT001");
    }

    private async Task UnknownClass_ProducesType001()
    {
        using var fixture = TestFixture.Create();
        fixture.WriteTargetDef("Defs.xml", "<Defs><ThingDef Class=\"Missing.Type\"><defName>BadClass</defName><statBase>1</statBase></ThingDef></Defs>");

        var result = await ValidateAsync(fixture);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "TYPE001");
    }

    private async Task FieldTypeMismatch_ProducesType005()
    {
        using var fixture = TestFixture.Create();
        fixture.WriteTargetDef("Defs.xml", "<Defs><ThingDef><defName>BadStat</defName><statBase>oops</statBase></ThingDef></Defs>");

        var cli = await RunCliAsync(fixture.CreateCliArgs());
        Assert.Equal(1, cli.ExitCode);
        Assert.Contains(fixture.Normalize(cli.StdOut), "TYPE005");
    }

    private async Task CustomAssemblyAndCrossModReference_Succeeds()
    {
        using var fixture = TestFixture.Create(includeDependency: true, includeCustomAssembly: true);
        fixture.WriteTargetDef(
            "Defs.xml",
            """
            <Defs>
              <ThingDef Class="TestModTypes.CustomThingDef">
                <defName>CustomOk</defName>
                <statBase>5</statBase>
                <sound>DepBeep</sound>
                <accentColor>BaseBlue</accentColor>
                <customPayload>
                  <number>7</number>
                </customPayload>
              </ThingDef>
            </Defs>
            """);

        var result = await ValidateAsync(fixture);
        Assert.Equal(0, result.Summary.ErrorCount);
    }


    private async Task FiltersOutExternalContextNoise()
    {
        using var fixture = TestFixture.Create();
        fixture.WriteCoreDef("Noise.xml", "<Defs><ThingDef ParentName=\"MissingCoreParent\"><defName>CoreNoise</defName><statBase>1</statBase></ThingDef></Defs>");
        fixture.WriteTargetDef("Defs.xml", "<Defs><ThingDef><defName>TargetOk</defName><statBase>1</statBase></ThingDef></Defs>");

        var cli = await RunCliAsync(fixture.CreateCliArgs());
        Assert.Equal(0, cli.ExitCode);
        Assert.DoesNotContain(fixture.Normalize(cli.StdOut), "CoreNoise");
        Assert.DoesNotContain(fixture.Normalize(cli.StdOut), "INHERIT001");
    }

    private async Task DotEnv_FillsGameDirAndModsConfig()
    {
        using var fixture = TestFixture.Create();
        fixture.WriteTargetDef("Defs.xml", "<Defs><ThingDef><defName>FromDotEnv</defName><statBase>1</statBase></ThingDef></Defs>");
        fixture.WriteDotEnv("""
DEFVALIDATOR_GAME_DIR=__GAME_DIR__
DEFVALIDATOR_MODS_CONFIG=__MODS_CONFIG__
""");

        var cli = await RunCliAsync([fixture.TargetModPath], fixture.WorkingDirectory);
        Assert.Equal(0, cli.ExitCode);
    }

    private static async Task<ValidationResult> ValidateAsync(TestFixture fixture)
    {
        var engine = new DefValidationEngine();
        return await engine.ValidateAsync(fixture.CreateOptions(), CancellationToken.None);
    }

    private static async Task<CliResult> RunCliAsync(string[] args, string? workingDirectory = null)
    {
        var repoRoot = TestFixture.FindRepoRoot();
        var cliDll = Path.Combine(repoRoot, "src", "DefValidator.Cli", "bin", "Debug", "net10.0", "defvalidator.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? repoRoot
        };

        startInfo.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start CLI process.");
        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, stdOut, stdErr);
    }

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);
}

internal sealed class TestFixture : IDisposable
{
    private readonly string _root;
    private readonly string _gameDir;
    private readonly string _modsConfigPath;
    private readonly string _targetModPath;
    public string WorkingDirectory => _root;
    public string TargetModPath => _targetModPath;

    private TestFixture(string root, string gameDir, string modsConfigPath, string targetModPath)
    {
        _root = root;
        _gameDir = gameDir;
        _modsConfigPath = modsConfigPath;
        _targetModPath = targetModPath;
    }

    public static TestFixture Create(bool includeDependency = false, bool includeCustomAssembly = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "defvalidator-tests", Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(root, "Game");
        var modsConfigPath = Path.Combine(root, "ModsConfig.xml");
        var targetModPath = Path.Combine(root, "TargetMod");

        Directory.CreateDirectory(root);
        CreateCore(gameDir);
        CreateTarget(targetModPath, includeCustomAssembly);
        if (includeDependency)
        {
            CreateDependency(gameDir);
        }

        File.WriteAllText(
            modsConfigPath,
            includeDependency
                ? "<ModsConfigData><activeMods><li>ludeon.rimworld</li><li>dep.mod</li></activeMods></ModsConfigData>"
                : "<ModsConfigData><activeMods><li>ludeon.rimworld</li></activeMods></ModsConfigData>");

        return new TestFixture(root, gameDir, modsConfigPath, targetModPath);
    }

    public ValidationOptions CreateOptions() => new(_targetModPath, _gameDir, _modsConfigPath, Strict: false);

    public string[] CreateCliArgs(params string[] extraArgs)
    {
        var args = new List<string>
        {
            _targetModPath,
            "--game-dir",
            _gameDir,
            "--mods-config",
            _modsConfigPath
        };
        args.AddRange(extraArgs);
        return args.ToArray();
    }

    public void WriteTargetDef(string fileName, string xml)
    {
        var fullPath = Path.Combine(_targetModPath, "Defs", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, xml);
    }

    public void WriteCoreDef(string fileName, string xml)
    {
        var fullPath = Path.Combine(_gameDir, "Data", "Core", "Defs", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, xml);
    }

    public void WriteDotEnv(string content)
    {
        var normalized = content
            .Replace("__GAME_DIR__", _gameDir, StringComparison.Ordinal)
            .Replace("__MODS_CONFIG__", _modsConfigPath, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_root, ".env"), normalized);
    }

    public string Normalize(string value) => value.Replace(_root, "$ROOT", StringComparison.OrdinalIgnoreCase).Replace("\r\n", "\n");

    public static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DefValidator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static void CreateCore(string gameDir)
    {
        var core = Path.Combine(gameDir, "Data", "Core");
        Directory.CreateDirectory(Path.Combine(core, "About"));
        Directory.CreateDirectory(Path.Combine(core, "Defs"));
        Directory.CreateDirectory(Path.Combine(core, "Assemblies"));
        File.WriteAllText(Path.Combine(core, "About", "About.xml"), "<ModMetaData><name>Core</name><packageId>ludeon.rimworld</packageId></ModMetaData>");
        File.WriteAllText(Path.Combine(core, "LoadFolders.xml"), "<loadFolders><li>.</li></loadFolders>");
        File.WriteAllText(Path.Combine(core, "Defs", "CoreDefs.xml"), "<Defs><SoundDef><defName>CoreSound</defName></SoundDef><ColorDef><defName>BaseBlue</defName><rgb>0000ff</rgb></ColorDef></Defs>");
        File.Copy(Path.Combine(FindRepoRoot(), "tests", "FixtureVerse", "bin", "Debug", "net10.0", "Verse.dll"), Path.Combine(core, "Assemblies", "Verse.dll"), overwrite: true);
    }

    private static void CreateDependency(string gameDir)
    {
        var mod = Path.Combine(gameDir, "Mods", "DependencyMod");
        Directory.CreateDirectory(Path.Combine(mod, "About"));
        Directory.CreateDirectory(Path.Combine(mod, "Defs"));
        File.WriteAllText(Path.Combine(mod, "About", "About.xml"), "<ModMetaData><name>Dep</name><packageId>dep.mod</packageId></ModMetaData>");
        File.WriteAllText(Path.Combine(mod, "Defs", "Defs.xml"), "<Defs><SoundDef><defName>DepBeep</defName></SoundDef></Defs>");
    }

    private static void CreateTarget(string targetModPath, bool includeCustomAssembly)
    {
        Directory.CreateDirectory(Path.Combine(targetModPath, "About"));
        Directory.CreateDirectory(Path.Combine(targetModPath, "Defs"));
        File.WriteAllText(Path.Combine(targetModPath, "About", "About.xml"), "<ModMetaData><name>Target</name><packageId>target.mod</packageId><modDependencies><li>dep.mod</li></modDependencies></ModMetaData>");
        File.WriteAllText(Path.Combine(targetModPath, "LoadFolders.xml"), "<loadFolders><li>.</li></loadFolders>");

        if (!includeCustomAssembly)
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(targetModPath, "Assemblies"));
        File.Copy(Path.Combine(FindRepoRoot(), "tests", "FixtureModTypes", "bin", "Debug", "net10.0", "TestModTypes.dll"), Path.Combine(targetModPath, "Assemblies", "TestModTypes.dll"), overwrite: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
        }
    }

    public static void Contains(string value, string expectedSubstring)
    {
        if (!value.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected substring '{expectedSubstring}' in: {value}");
        }
    }

    public static void DoesNotContain(string value, string unexpectedSubstring)
    {
        if (value.Contains(unexpectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Did not expect substring '{unexpectedSubstring}' in: {value}");
        }
    }

    public static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate))
        {
            throw new InvalidOperationException("Expected sequence to contain a matching item.");
        }
    }
}
