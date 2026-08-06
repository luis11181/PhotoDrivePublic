using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MediaExplorer.Media.Api.Http;

internal static class MediaResultHttpExtensions
{
    public static IActionResult ToApiResult<T>(
        this ControllerBase controller,
        Result<T> result,
        int successStatusCode = StatusCodes.Status200OK)
        where T : notnull => result.Match<IActionResult>(
            onSuccess: value => controller.StatusCode(successStatusCode, ApiRes<T>.Success(value)),
            onFailure: error => controller.ToApiError(error));

    public static IActionResult ToApiError(this ControllerBase controller, ResultError error) => error.ErrorCode switch
    {
        MediaErrors.NotFoundCode => controller.NotFound(ApiRes.NotFound(error.Message)),
        MediaErrors.ConflictCode => controller.Conflict(ApiRes.Conflict(error.Message)),
        _ => controller.BadRequest(ApiRes.BadRequest(error.Message))
    };
}
