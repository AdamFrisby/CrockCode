namespace CrockCode.Core.Domain;

/// <summary>
/// Closed union representing a computation that either succeeds with <typeparamref name="T"/>
/// or fails with an <see cref="Error"/>. Supports LINQ composition via Select/SelectMany.
/// </summary>
public abstract record Result<T>
{
    private Result() { }

    public abstract TOut Match<TOut>(Func<T, TOut> ok, Func<Error, TOut> err);

    public bool IsOk => this is Ok;
    public bool IsErr => this is Err;

    /// <summary>Unwraps the value or throws if Err. Use only in tests.</summary>
    public T Unwrap() => Match(v => v, e => throw new InvalidOperationException($"Unwrap called on Err: {e}"));

    /// <summary>Unwraps the error or throws if Ok.</summary>
    public Error UnwrapErr() => Match(_ => throw new InvalidOperationException("UnwrapErr called on Ok"), e => e);

    public Result<TOut> Map<TOut>(Func<T, TOut> f) =>
        Match<Result<TOut>>(v => new Result<TOut>.Ok(f(v)), e => new Result<TOut>.Err(e));

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> f) =>
        Match(f, e => new Result<TOut>.Err(e));

    // LINQ support
    public Result<TOut> Select<TOut>(Func<T, TOut> f) => Map(f);

    public Result<TOut> SelectMany<TMid, TOut>(
        Func<T, Result<TMid>> bind,
        Func<T, TMid, TOut> project) =>
        Match(
            v => bind(v).Match<Result<TOut>>(
                m => new Result<TOut>.Ok(project(v, m)),
                e => new Result<TOut>.Err(e)),
            e => new Result<TOut>.Err(e));

    public sealed record Ok(T Value) : Result<T>
    {
        public override TOut Match<TOut>(Func<T, TOut> ok, Func<Error, TOut> err) => ok(Value);
    }

    public sealed record Err(Error Error) : Result<T>
    {
        public override TOut Match<TOut>(Func<T, TOut> ok, Func<Error, TOut> err) => err(Error);
    }
}

/// <summary>
/// Factory methods for creating Result values without specifying the generic type.
/// </summary>
public static class Result
{
    public static Result<T> Ok<T>(T value) => new Result<T>.Ok(value);
    public static Result<T> Err<T>(Error error) => new Result<T>.Err(error);
}
