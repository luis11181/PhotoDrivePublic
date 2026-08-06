using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Contracts.APICommons.CommonExceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CSSApps.Shared.Utils.ApiHelpers.ErrorHandlers.Configuration;

internal sealed class BadRequestExceptionHandler : IExceptionHandler
{
    private readonly ILogger<BadRequestExceptionHandler> _logger;

    public BadRequestExceptionHandler(ILogger<BadRequestExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadRequestException badRequestException) return false;

        _logger.LogInformation(exception,
            "Req Path: {reqPath}, Message: {errorMessage}, Req method: {reqMethod}, Error details: {details}",
            httpContext.Request.Path, exception.Message, httpContext.Request.Method, exception.StackTrace ?? "");

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad Request",
            Detail = badRequestException.Message
        };

        var res = ApiRes.Failure(problemDetails);

        httpContext.Response.StatusCode = problemDetails.Status.Value;

        await httpContext.Response
            .WriteAsJsonAsync(res, cancellationToken);

        return true;
    }
}