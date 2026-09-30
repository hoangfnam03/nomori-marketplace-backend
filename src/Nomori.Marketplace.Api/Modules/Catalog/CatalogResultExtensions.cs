using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Api.Modules.Catalog;

internal static class CatalogResultExtensions
{
    /// <summary>Maps a failed result to 400 (field errors), 403, 404 (not found) or 409 (business rule; the code is in <c>detail</c>).</summary>
    public static IActionResult ToFailure<T>(this ControllerBase controller, CatalogResult<T> result)
    {
        if (result.Errors.Count > 0)
            return controller.BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));

        return result.ErrorCode switch
        {
            CatalogErrors.NotFound => controller.NotFound(),
            CatalogErrors.Forbidden => controller.StatusCode(StatusCodes.Status403Forbidden),
            _ => controller.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Catalog operation failed",
                Detail = result.ErrorCode
            })
        };
    }
}
