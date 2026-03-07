namespace DefValidator.Core;

public enum DiagnosticSeverity {
    Warning,
    Error
}

public enum ValidationStage {
    Context,
    XmlLoad,
    Inheritance,
    Type,
    Xref,
    Rule
}

public sealed record ValidationOptions(
    string ModPath,
    string GameDirectory);

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

public sealed record ValidationSummary(int ErrorCount) { }

public sealed record ValidationResult(ValidationSummary Summary, IReadOnlyList<Diagnostic> Diagnostics) {
    public int GetExitCode() => Summary.ErrorCount > 0 ? 1 : 0;
}

public sealed record ValidationTiming(string Name, TimeSpan Elapsed, int Count = 1);

public sealed record ValidationRun(ValidationResult Result, IReadOnlyList<ValidationTiming> Timings);

internal sealed class DiagnosticBag {
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
        string? defName = null) {
        _items.Add(new Diagnostic(code, severity, message, file, line, column, packageId, defType, defName, stage));
    }

    public ValidationResult ToResult() {
        var summary = new ValidationSummary(
            _items.Count(static item => item.Severity == DiagnosticSeverity.Error));

        return new ValidationResult(summary, _items);
    }
}
