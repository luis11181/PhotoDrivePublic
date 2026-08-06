using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Contracts.APICommons.CommonExceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CSSApps.Shared.Utils.ApiHelpers.ErrorHandlers.Configuration;

internal sealed class NotFoundExceptionHandler : IExceptionHandler
{
    private readonly ILogger<NotFoundExceptionHandler> _logger;

    public NotFoundExceptionHandler(ILogger<NotFoundExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not NotFoundException notFoundException) return false;

        _logger.LogInformation(exception,
            "Req Path: {reqPath}, Message: {errorMessage}, Req method: {reqMethod}, Error details: {details}",
            httpContext.Request.Path, exception.Message, httpContext.Request.Method, exception.StackTrace ?? "");

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Not Found",
            Detail = notFoundException.Message
        };

        var res = ApiRes.Failure(problemDetails);

        httpContext.Response.StatusCode = problemDetails.Status.Value;

        await httpContext.Response
            .WriteAsJsonAsync(res, cancellationToken);

        return true;
    }
}