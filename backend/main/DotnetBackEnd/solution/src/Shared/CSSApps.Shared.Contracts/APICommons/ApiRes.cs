using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CSSApps.Shared.Contracts.APICommons;

/// <summary>
///     Represents a standardized response for an API, encapsulating the data, success status, and potential errors.
/// </summary>
/// <typeparam name="T">The type of the data payload in a successful response.</typeparam>
public class ApiRes<T>
    where T : notnull
{
    public ApiRes(bool successful, T data, ProblemDetails? error = null)
    {
        Successful = successful;
        Data = data;
        Error = error;
        if (successful && error is not null)
            throw new InvalidOperationException("Successful response cannot contain an error");

        //if (!successful && error is null) { throw new InvalidOperationException("Unsuccessful response must contain an error"); }

        if (!successful && data is not null)
            throw new InvalidOperationException("Unsuccessful response cannot contain data");
    }

    /// <summary>
    ///     Indicates whether the API call was successful.
    /// </summary>
    public bool Successful { get; set; }

    /// <summary>
    ///     The data payload returned by the API in case of a successful response.
    /// </summary>
    public T Data { get; set; }

    public ProblemDetails? Error { get; set; }

    /// <summary>
    ///     Creates a successful ApiResponse object with the specified data.
    /// </summary>
    /// <param name="data">The data payload for the successful response.</param>
    /// <returns>An ApiResponse object indicating success with the specified data.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory method pattern")]
    public static ApiRes<T> Success(T data)
    {
        return new ApiRes<T>(true, data);
    }
}

public class ApiRes
{
    public ApiRes(bool successful, string data, ProblemDetails? error = null)
    {
        Successful = successful;
        Data = data;
        Error = error;
        if (successful && error is not null)
            throw new InvalidOperationException("Successful response cannot contain an error");

        if (!successful && data is not null)
            throw new InvalidOperationException("Unsuccessful response cannot contain data");

        //if (!successful && error is null) { throw new InvalidOperationException("Unsuccessful response must contain an error"); }
    }

    /// <summary>
    ///     Indicates whether the API call was successful.
    /// </summary>
    public bool Successful { get; set; }

    /// <summary>
    ///     The data payload returned by the API in case of a successful response.
    /// </summary>
    public string Data { get; set; }

    public ProblemDetails? Error { get; set; }

    /// <summary>
    ///     Creates a successful ApiResponse object with the default success message.
    /// </summary>
    /// <returns>An ApiResponse object indicating success with the default success message.</returns>
    public static ApiRes Success()
    {
        return new ApiRes(true, "Success");
    }

    /// <summary>
    ///     Creates a successful ApiResponse object with the specified data.
    /// </summary>
    /// <param name="data">The data payload for the successful response.</param>
    /// <returns>An ApiResponse object indicating success with the specified data.</returns>
    public static ApiRes Success(string data)
    {
        return new ApiRes(true, data);
    }

    /// <summary>
    ///     Creates an unsuccessful ApiResponse object with the specified error.
    /// </summary>
    /// <param name="error">The error details for the unsuccessful response.</param>
    /// <returns>An ApiResponse object indicating failure with the specified error.</returns>
    public static ApiRes Failure(ProblemDetails error)
    {
        return new ApiRes(false, default!, error);
    }

    public static ApiRes BadRequest(string errorMessage = "Bad Request")
    {
        var error = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad Request",
            Detail = errorMessage
        };

        return new ApiRes(false, default!, error);
    }

    public static ApiRes NotFound(string errorMessage = "Not Found")
    {
        var error = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Not Found",
            Detail = errorMessage
        };

        return new ApiRes(false, default!, error);
    }

    public static ApiRes Unauthorized(string errorMessage = "Unauthorized")
    {
        var error = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = errorMessage
        };

        return new ApiRes(false, default!, error);
    }

    public static ApiRes InternalServerError(string errorMessage = "Internal Server Error")
    {
        var error = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Internal Server Error",
            Detail = errorMessage
        };

        return new ApiRes(false, default!, error);
    }

    public static ApiRes Conflict(string errorMessage = "Conflict")
    {
        var error = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Conflict",
            Detail = errorMessage
        };

        return new ApiRes(false, default!, error);
    }

    public static ApiRes RangeNotSatisfiable(string errorMessage = "The requested byte range is not satisfiable")
    {
        var error = new ProblemDetails
        {
            Status = StatusCodes.Status416RangeNotSatisfiable,
            Title = "Range Not Satisfiable",
            Detail = errorMessage
        };

        return new ApiRes(false, default!, error);
    }
}
