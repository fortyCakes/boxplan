namespace BoxPlanLib;

public sealed class ParseResult<T>
{
    public T? Value { get; }
    public IReadOnlyList<PlanError> Errors { get; }

    public bool Success => Value is not null && !Errors.Any(e => e.Severity == Severity.Error);

    public ParseResult(T? value, IEnumerable<PlanError> errors)
    {
        Value = value;
        Errors = errors.ToArray();
    }

    public static ParseResult<T> Ok(T value) => new(value, Array.Empty<PlanError>());

    public static ParseResult<T> Ok(T value, IEnumerable<PlanError> warnings) => new(value, warnings);

    public static ParseResult<T> Fail(params PlanError[] errors) => new(default, errors);

    public static ParseResult<T> Fail(IEnumerable<PlanError> errors) => new(default, errors);
}
