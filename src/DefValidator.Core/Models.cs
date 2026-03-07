namespace DefValidator.Core;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum ValidationStage
{
    Cli,
    Context,
    XmlLoad,
    Inheritance,
    Type,
    Xref,
    Rule
}

public sealed record ValidationOptions(
    string ModPath,
    string GameDirectory,
    string ModsConfigPath,
    bool Strict);

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? File,
    int? Line,
    int? Column,
    string? PackageId,
    string? DefType,
    string? DefName,
    ValidationStage Stage);

public sealed record ValidationSummary(int ErrorCount, int WarningCount, int InfoCount)
{
    public bool HasErrors => ErrorCount > 0;
}

public sealed record ValidationResult(ValidationSummary Summary, IReadOnlyList<Diagnostic> Diagnostics)
{
    public int GetExitCode(bool strict)
    {
        if (Summary.ErrorCount > 0)
        {
            return 1;
        }

        if (strict && Summary.WarningCount > 0)
        {
            return 1;
        }

        return 0;
    }
}

internal sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public void Add(
        string code,
        DiagnosticSeverity severity,
        string message,
        ValidationStage stage,
        string? file = null,
        int? line = null,
        int? column = null,
        string? packageId = null,
        string? defType = null,
        string? defName = null)
    {
        _items.Add(new Diagnostic(code, severity, message, file, line, column, packageId, defType, defName, stage));
    }

    public IReadOnlyList<Diagnostic> Items => _items;

    public ValidationResult ToResult()
    {
        var summary = new ValidationSummary(
            _items.Count(static item => item.Severity == DiagnosticSeverity.Error),
            _items.Count(static item => item.Severity == DiagnosticSeverity.Warning),
            _items.Count(static item => item.Severity == DiagnosticSeverity.Info));

        return new ValidationResult(summary, _items);
    }
}
