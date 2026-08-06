namespace CSSApps.Shared.Contracts.Primitives;

#pragma warning disable CA1000 // Do not declare static members on generic types
/// <summary>
///     Implementation of the result type pattern.
/// </summary>
/// <typeparam name="TSuccess"></typeparam>
public readonly struct Result<TSuccess> : IEquatable<Result<TSuccess>> //where TSuccess : notnull
{
    private readonly TSuccess _successValue;
    private readonly ResultError? _errorValue;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, TSuccess successValue, ResultError errorValue)
    {
        IsSuccess = isSuccess;
        _successValue = successValue;
        _errorValue = errorValue;
    }


    public TSuccess SuccessValue => IsSuccess
        ? _successValue
        : throw new InvalidOperationException("Attempt to access SuccessValue of a failed Result.");

    public ResultError ErrorValue => IsFailure
        ? _errorValue!
        : throw new InvalidOperationException("Attempt to access ErrorValue of a successful Result.");

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory method pattern")]
    public static Result<TSuccess> Success(TSuccess value)
    {
        return new Result<TSuccess>(true, value, default!);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory method pattern")]
    public static Result<TSuccess> Failure(ResultError error)
    {
        return new Result<TSuccess>(false, default!, error);
    }

    public TResult Match<TResult>(Func<TSuccess, TResult> onSuccess, Func<ResultError, TResult> onFailure)
    {
        return IsSuccess ? onSuccess(_successValue) : onFailure(_errorValue!);
    }

    public Result<TSuccess> OnSuccess(Action<TSuccess> action)
    {
        if (IsSuccess)
            action(SuccessValue);

        return this;
    }

    public Result<TSuccess> OnFailure(Action<ResultError> action)
    {
        if (IsFailure)
            action(ErrorValue);

        return this;
    }

    // Async support for OnSuccess and OnFailure
    public async Task<Result<TSuccess>> OnSuccessAsync(Func<TSuccess, Task> asyncAction)
    {
        if (IsSuccess) await asyncAction(_successValue);
        return this;
    }

    public async Task<Result<TSuccess>> OnFailureAsync(Func<ResultError, Task> asyncAction)
    {
        if (IsFailure) await asyncAction(_errorValue!);
        return this;
    }

    // Support for pattern matching
    public void Deconstruct(out bool isSuccess, out TSuccess? successValue, out ResultError? errorValue)
    {
        isSuccess = IsSuccess;
        successValue = _successValue;
        errorValue = _errorValue;
    }

    public static implicit operator Result<TSuccess>(TSuccess v)
    {
        return new Result<TSuccess>(true, v, default!);
    }

    public static Result<TSuccess> FromSuccess(TSuccess v)
    {
        return new Result<TSuccess>(true, v, default!);
    }

    public static Result<TSuccess> ToResult(TSuccess v) => FromSuccess(v);

    public static implicit operator Result<TSuccess>(ResultError e)
    {
        return new Result<TSuccess>(false, default!, e);
    }

    public static Result<TSuccess> FromResultError(ResultError e)
    {
        return new Result<TSuccess>(false, default!, e);
    }

    public override bool Equals(object? obj)
    {
        return obj is Result<TSuccess> other && Equals(other);
    }

    public bool Equals(Result<TSuccess> other)
    {
        if (IsSuccess != other.IsSuccess) return false;
        if (IsSuccess) return EqualityComparer<TSuccess>.Default.Equals(_successValue, other._successValue);
        return EqualityComparer<ResultError>.Default.Equals(_errorValue, other._errorValue);
    }

    public override int GetHashCode()
    {
        return IsSuccess ? _successValue?.GetHashCode() ?? 0 : _errorValue?.GetHashCode() ?? 0;
    }

    public static bool operator ==(Result<TSuccess> left, Result<TSuccess> right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Result<TSuccess> left, Result<TSuccess> right)
    {
        return !left.Equals(right);
    }
}

//* NON generic version of ResultError
/// <summary>
///     Implementation of the result type pattern.
/// </summary>
public readonly struct Result : IEquatable<Result>
{
    private readonly ResultError? _errorValue;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, ResultError errorValue)
    {
        IsSuccess = isSuccess;
        _errorValue = errorValue;
    }

    public ResultError ErrorValue => IsFailure
        ? _errorValue!
        : throw new InvalidOperationException("Attempt to access ErrorValue of a successful Result.");

    public static Result Success()
    {
        return new Result(true, default!);
    }

    public static Result Failure(ResultError error)
    {
        return new Result(false, error);
    }

    public TResult Match<TResult>(Func<TResult> onSuccess, Func<ResultError, TResult> onFailure)
    {
        return IsSuccess ? onSuccess() : onFailure(_errorValue!);
    }

    public void OnSuccess(Action action)
    {
        if (IsSuccess)
            action();
    }

    public void OnFailure(Action<ResultError> action)
    {
        if (IsFailure)
            action(ErrorValue);
    }

    // Async support for OnSuccess and OnFailure
    public async Task OnSuccessAsync(Func<Task> asyncAction)
    {
        if (IsSuccess) await asyncAction();
    }

    public async Task OnFailureAsync(Func<ResultError, Task> asyncAction)
    {
        if (IsFailure) await asyncAction(_errorValue!);
    }

    // Support for pattern matching
    public void Deconstruct(out bool isSuccess, out ResultError? errorValue)
    {
        isSuccess = IsSuccess;
        errorValue = _errorValue;
    }

    public static implicit operator Result(ResultError e)
    {
        return new Result(false, e);
    }

    public static Result FromResultError(ResultError e)
    {
        return new Result(false, e);
    }

    public override bool Equals(object? obj)
    {
        return obj is Result other && Equals(other);
    }

    public bool Equals(Result other)
    {
        if (IsSuccess != other.IsSuccess) return false;
        if (IsSuccess) return true;
        return EqualityComparer<ResultError>.Default.Equals(_errorValue, other._errorValue);
    }

    public override int GetHashCode()
    {
        return IsSuccess ? 1 : _errorValue?.GetHashCode() ?? 0;
    }

    public static bool operator ==(Result left, Result right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Result left, Result right)
    {
        return !left.Equals(right);
    }
}