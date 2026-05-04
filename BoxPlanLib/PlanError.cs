namespace BoxPlanLib;

public enum Severity
{
    Warning,
    Error,
}

public sealed record PlanError(
    Severity Severity,
    string Message,
    string? Path = null,
    int? Line = null,
    int? Column = null)
{
    public override string ToString()
    {
        var loc = Line is { } l
            ? Column is { } c ? $" (line {l}, col {c})" : $" (line {l})"
            : "";
        var path = Path is null ? "" : $" at {Path}";
        return $"{Severity}{loc}{path}: {Message}";
    }
}
