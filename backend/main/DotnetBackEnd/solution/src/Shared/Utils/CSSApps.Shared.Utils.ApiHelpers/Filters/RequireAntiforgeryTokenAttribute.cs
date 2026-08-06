using CSSApps.Shared.Contracts.APICommons;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace CSSApps.Shared.Utils.ApiHelpers.Filters;

/// <summary>
/// Validates the configured antiforgery cookie and request header for API mutations.
/// This intentionally avoids MVC's Razor/ViewFeatures antiforgery filter.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireAntiforgeryTokenAttribute : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    // Run after the built-in authorization filter so anonymous requests remain 401 responses.
    public int Order => 1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestObjectResult(ApiRes.BadRequest("Invalid or missing CSRF token."));
        }
    }
}
