using CSSApps.Shared.Contracts.Constants;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace CSSApps.Shared.Infrastructure;

public class HttpContextEnricher(IHttpContextAccessor httpContextAccessor) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var correlationId = httpContext.Request.Headers[CommonConstants.Headers.CorrelationHeaderKey].ToString();
            var requestId = httpContext.TraceIdentifier;

            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CorrelationId", correlationId));
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("RequestId", requestId));
        }
    }
}