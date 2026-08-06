using CSSApps.Shared.Contracts.APICommons;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSSApps.Shared.Utils.ApiHelpers.ErrorHandlers.Configuration;

internal sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IHostEnvironment _env;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception,
            "Req Path: {reqPath}, Message: {errorMessage}, Req method: {reqMethod}, Error details: {details}",
            httpContext.Request.Path, exception.Message, httpContext.Request.Method, exception.StackTrace ?? "");

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Server error",
            Detail = _env.IsDevelopment() ? exception.Message : "An error occurred while processing your request",
            Instance = _env.IsDevelopment() ? httpContext.Request.Path : null
        };

        var res = ApiRes.Failure(problemDetails);

        httpContext.Response.StatusCode = problemDetails.Status.Value;

        await httpContext.Response
            .WriteAsJsonAsync(res, cancellationToken);

        return true;
    }
}

//middleware approach
//internal sealed class ExceptionMiddleware
//{
//  private readonly RequestDelegate _next;
//  private readonly ILogger<ExceptionMiddleware> _logger;
//  private readonly IHostEnvironment _env;
//  public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger,
//      IHostEnvironment env)
//  {
//    _env = env;
//    _logger = logger;
//    _next = next;
//  }

//public async Task InvokeAsync(HttpContext context)
//{
//  try
//  {
//    //*execute the request. if there is an exception it will be catched by the catch block
//    //if there is another catch block in the controller it will be executed, and this catch block will be ignored
//    await _next(context);
//  }
//  catch (Exception ex)
//  {

//    try
//    {
//      string reqMethod = context.Request.Method;
//      string reqPath = context.Request.Path;
//      string errorDetails = ex.StackTrace?.ToString() ?? "Details not available in production response";

//      string? errorMessage = ex.Message ?? "Details not available in production response";

//      errorMessage = _env.IsDevelopment() ? errorMessage : "";

//      context.Response.ContentType = "application/json";

//      _logger.LogError(ex, "Req Path: {reqPath}, Message: {errorMessage}, Req method: {reqMethod}, Error details: {details}", reqPath, ex.Message, reqMethod, errorDetails);

//      var response = new APIResponse
//      {
//        IsSuccess = false,
//        ErrorMessages = [errorMessage ?? ""]
//      };

//      var options = new JsonSerializerOptions
//      {
//        PropertyNamingPolicy =
//          JsonNamingPolicy.CamelCase
//      };

//      var json = JsonSerializer.Serialize(response, options);

//      await context.Response.WriteAsync(json);

//    }
//    catch
//    {
//      _logger.LogError(ex, "Error in ExceptionMiddleware");
//    }
//  }
//  }
//}