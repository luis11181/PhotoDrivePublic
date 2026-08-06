namespace CSSApps.Shared.Contracts.Primitives;

/// <summary>
///     Represents an error that can be returned in a Result object.
///     This is a lightweight record type - not an exception.
/// </summary>
public record ResultError
{
    public string Message { get; }
    public string ErrorCode { get; }

    public ResultError()
    {
        Message = string.Empty;
        ErrorCode = "GENERIC_ERROR";
    }

    public ResultError(string message, string errorCode = "GENERIC_ERROR")
    {
        Message = message;
        ErrorCode = errorCode;
    }

    public override string ToString() => $"[{ErrorCode}] {Message}";
}


public static class CommonResultErrors
{
    public static readonly ResultError FindByIdOperationReturnNull = new(
        "Find by Id Operation returned a null result", "NOT_FOUND");

    public static readonly ResultError FailureCreatingRecord = new(
        "The creation operation returned a null record", "CREATION_FAILED");

    public static readonly ResultError OperationCancelled = new(
        "The operation was cancelled (like the api caller leaving the page)", "OPERATION_CANCELLED");

    // Domain-specific errors with clear codes
    public static readonly ResultError InvalidEmail = new(
        "The provided email address is invalid", "INVALID_EMAIL");

}