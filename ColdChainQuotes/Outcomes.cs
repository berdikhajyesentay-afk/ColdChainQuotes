namespace ColdChainQuotes;

// Codes are stable for callers; messages identify the relevant item or operation.
public sealed record AppError(string Code, string Message);

// No shared Value property: only Success owns a payload, only Failure owns an error.
public abstract class Result<T> where T : notnull
{
    private Result() { }
    public sealed class Success : Result<T>
    {
        public T Value { get; }
        internal Success(T value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }
    }
    public sealed class Failure : Result<T>
    {
        public AppError Error { get; }
        internal Failure(AppError error)
        {
            ArgumentNullException.ThrowIfNull(error);
            Error = error;
        }
    }
    public static Result<T> Ok(T value) => new Success(value);
    public static Result<T> Fail(AppError error) => new Failure(error);

    public Result<U> Map<U>(Func<T, U> transform) where U : notnull => this switch
    {
        Success success => Result<U>.Ok(transform(success.Value)),
        Failure failure => Result<U>.Fail(failure.Error),
        _ => throw new InvalidOperationException("Unknown Result case")
    };
    public Result<U> Bind<U>(Func<T, Result<U>> next) where U : notnull => this switch
    {
        Success success => next(success.Value),
        Failure failure => Result<U>.Fail(failure.Error),
        _ => throw new InvalidOperationException("Unknown Result case")
    };
}

// Presence is a case, not a comparison with default(T). Some(0), Some(false) and Some("") exist.
public abstract class Option<T> where T : notnull
{
    private Option() { }
    public sealed class Found : Option<T>
    {
        public T Value { get; }
        internal Found(T value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }
    }
    public sealed class Missing : Option<T> { }
    public static Option<T> Some(T value) => new Found(value);
    public static Option<T> None() => new Missing();
    public Result<T> ToResult(AppError ifMissing) => this switch
    {
        Found found => Result<T>.Ok(found.Value),
        Missing => Result<T>.Fail(ifMissing),
        _ => throw new InvalidOperationException("Unknown Option case")
    };
}
