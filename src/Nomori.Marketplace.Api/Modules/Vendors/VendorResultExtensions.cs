using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Vendors;

internal static class VendorResultExtensions
{
    /// <summary>
    /// Maps a failed result to 400 (field errors), 403, 404 or 409 (business rule; the code is in <c>detail</c>).
    /// </summary>
    public static IActionResult ToFailure<T>(this ControllerBase controller, VendorResult<T> result, string conflictTitle)
    {
        if (result.Errors.Count > 0)
            return controller.BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));

        return result.ErrorCode switch
        {
            VendorErrors.NotFound => controller.NotFound(),
            VendorErrors.Forbidden => controller.StatusCode(StatusCodes.Status403Forbidden),
            _ => controller.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = conflictTitle,
                Detail = result.ErrorCode
            })
        };
    }
}
