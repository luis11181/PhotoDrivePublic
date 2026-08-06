using CSSApps.Shared.Contracts.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CSSApps.Shared.Utils.ApiHelpers.Middlewares;

public class CorrelationIdMiddleware
{
    public const string CorrelationHeaderKey = CommonConstants.Headers.CorrelationHeaderKey;
    private readonly ILogger<CorrelationIdMiddleware> _logger;
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        var existingCorrelationId = context.Request?.Headers.ContainsKey(CorrelationHeaderKey);

        string correlationId;

        if (existingCorrelationId.HasValue && existingCorrelationId.Value &&
            !string.IsNullOrEmpty(context.Request?.Headers[CorrelationHeaderKey].ToString()))
            correlationId = context.Request?.Headers[CorrelationHeaderKey].ToString()!;
        else
            correlationId = Guid.NewGuid().ToString();

        context.Request?.Headers.Append(CorrelationHeaderKey, correlationId);
        context.Response.Headers.Append(CorrelationHeaderKey, correlationId);

        await _next.Invoke(context);
    }
}