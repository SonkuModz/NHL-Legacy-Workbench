namespace NhlWorkbench.Core.Diagnostics;

/// <summary>
/// Represents the outcome of an operation that must never throw across a public Core
/// boundary (file parsing, container writing, DDS codec calls). Malformed input is a
/// Failure, not an exception.
/// </summary>
public sealed class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public Exception? Exception { get; }
    public IReadOnlyList<ParseWarning> Warnings { get; }

    private Result(bool isSuccess, T? value, string? error, Exception? exception, IReadOnlyList<ParseWarning>? warnings)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        Exception = exception;
        Warnings = warnings ?? Array.Empty<ParseWarning>();
    }

    public static Result<T> Success(T value, IReadOnlyList<ParseWarning>? warnings = null) =>
        new(true, value, null, null, warnings);

    public static Result<T> Failure(string error, Exception? exception = null) =>
        new(false, default, error, exception, null);
}

/// <summary>Non-generic helpers so callers can write <c>Result.Success(value)</c> without repeating the type argument.</summary>
public static class Result
{
    public static Result<T> Success<T>(T value, IReadOnlyList<ParseWarning>? warnings = null) =>
        Result<T>.Success(value, warnings);

    public static Result<T> Failure<T>(string error, Exception? exception = null) =>
        Result<T>.Failure(error, exception);
}
