namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// A result of an operation that has no value (success or a single <see cref="AppError"/>).
/// Hand-rolled (AI dev rule §143: no external Result framework).
/// </summary>
public readonly struct Result
{
    public bool IsSuccess { get; }
    public AppError? Error { get; }

    private Result(bool success, AppError? error)
    {
        IsSuccess = success;
        Error = error;
    }

    public static Result Success() => new(true, null);
    public static Result Failure(AppError error) => new(false, error ?? throw new System.ArgumentNullException(nameof(error)));

    public static implicit operator Result(AppError error) => Failure(error);

    public void Match(System.Action onSuccess, System.Action<AppError> onFailure)
    {
        if (IsSuccess)
            onSuccess();
        else
            onFailure(Error!);
    }
}

/// <summary>
/// A result carrying a value of type <typeparamref name="T"/> or a single <see cref="AppError"/>.
/// Hand-rolled (AI dev rule §143: no external Result framework).
/// </summary>
public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public AppError? Error { get; }

    private Result(bool success, T? value, AppError? error)
    {
        IsSuccess = success;
        Value = value;
        Error = error;
    }

    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(AppError error) => new(false, default, error ?? throw new System.ArgumentNullException(nameof(error)));

    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(AppError error) => Fail(error);

    /// <summary>Returns the value when successful, otherwise <paramref name="defaultValue"/>.</summary>
    public T ValueOrDefault(T defaultValue) => IsSuccess ? Value! : defaultValue;

    public TResult Match<TResult>(System.Func<T, TResult> onSuccess, System.Func<AppError, TResult> onFailure) =>
        IsSuccess ? onSuccess(Value!) : onFailure(Error!);

    public Result<TResult> Map<TResult>(System.Func<T, TResult> f) =>
        IsSuccess ? Result<TResult>.Ok(f(Value!)) : Result<TResult>.Fail(Error!);

    public Result<TResult> Bind<TResult>(System.Func<T, Result<TResult>> f) =>
        IsSuccess ? f(Value!) : Result<TResult>.Fail(Error!);

    public Result<T> Tap(System.Action<T> onSuccess)
    {
        if (IsSuccess)
            onSuccess(Value!);
        return this;
    }
}
