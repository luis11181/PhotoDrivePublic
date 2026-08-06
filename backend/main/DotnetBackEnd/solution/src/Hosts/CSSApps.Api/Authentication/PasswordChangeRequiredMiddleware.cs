using CSSApps.Shared.Contracts.APICommons;
using Microsoft.AspNetCore.Mvc;

namespace CSSApps.Api.Authentication;

/// <summary>
/// Restricts a session authenticated with a bootstrap password to account inspection,
/// password replacement, and logout until the temporary credential has been replaced.
/// </summary>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly string[] AllowedSuffixes =
    [
        "/users/me",
        "/users/set-password",
        "/users/logout"
    ];

    /// <summary>
    /// Enforces the restriction from the signed access-token claim before authorization reaches
    /// application endpoints; the replacement password issues a token without this claim.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var mustChange = context.User.HasClaim("must_change_password", "true");
        var allowed = AllowedSuffixes.Any(suffix =>
            context.Request.Path.Value?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);
        if (!mustChange || allowed)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(ApiRes.Failure(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Password change required",
            Detail = "Choose a new password before accessing the family library."
        }));
    }
}
