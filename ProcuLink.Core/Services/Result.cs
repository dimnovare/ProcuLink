namespace ProcuLink.Core.Services;

/// <summary>
/// Lightweight discriminated union for service-level outcomes.
/// Use <see cref="Result{T}.Ok"/> for success and <see cref="Result{T}.Fail"/> for
/// expected business errors. Never use for infrastructure exceptions — let those throw.
/// </summary>
public sealed class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }

    private Result(T value)          { IsSuccess = true;  Value = value; }
    private Result(string error)     { IsSuccess = false; Error = error; }

    public static Result<T> Ok(T value)      => new(value);
    public static Result<T> Fail(string err) => new(err);
}
