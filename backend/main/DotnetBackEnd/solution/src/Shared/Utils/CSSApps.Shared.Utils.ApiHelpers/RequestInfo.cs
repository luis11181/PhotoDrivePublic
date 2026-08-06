using CSSApps.Shared.Contracts.Constants;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;

namespace CSSApps.Shared.Utils.ApiHelpers;

public static class RequestInfo
{
  /// <summary>
  ///     Extracts the domain from request headers, considering load balancers and reverse proxies (e.g., CloudFront).
  /// </summary>
  public static string ExtractDomainFromRequest(HttpRequest Request)
    {
        // Check if we are behind a load balancer or reverse proxy (e.g., AWS CloudFront)
        var forwardedHost = Request.Headers[CommonConstants.Headers.ForwardedHostHeaderKey].FirstOrDefault();
        // also check X-Forwarded-For
        var forwardedFor = Request.Headers[CommonConstants.Headers.ForwardedForHeaderKey].FirstOrDefault();

        var origin = Request.Headers["Origin"].ToString();

        if (!string.IsNullOrEmpty(forwardedHost))
            // Use the forwarded host from the load balancer/reverse proxy
            return forwardedHost;

        if (!string.IsNullOrEmpty(forwardedFor))
            // Use the forwarded host from the load balancer/reverse proxy
            return forwardedFor;

        if (!string.IsNullOrEmpty(origin))
            // Fall back to the Origin header, which is used in cross-origin requests
            return new Uri(origin).Host;

        // Fallback to the request's own Host header
        return Request.Host.Host;
    }

    public static string GetClientIpAddress(HttpContext httpContext)
        => httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
}
