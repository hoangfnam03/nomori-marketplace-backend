using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Customer;

[ApiController]
[Route("api/v1/customer/profile")]
[Authorize]
public sealed class CustomerProfileController(
    ICurrentUser currentUser,
    ICustomerProfileService profileService) : ControllerBase
{
    [HttpGet]
    [HasPermission(PermissionCodes.CustomerProfileRead)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!TryGetCustomerId(out var customerId))
            return Unauthorized();

        var profile = await profileService.GetAsync(customerId, cancellationToken);
        return profile is null ? NotFound() : Ok(ToResponse(profile));
    }

    [HttpPut]
    [HasPermission(PermissionCodes.CustomerProfileManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(UpdateCustomerProfileRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCustomerId(out var customerId))
            return Unauthorized();

        var result = await profileService.UpdateAsync(new UpdateCustomerProfileCommand(
            customerId,
            request.FirstName,
            request.LastName,
            request.Gender,
            request.DateOfBirth,
            request.Phone), cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(pair => pair.Key, pair => pair.Value)));

        return Ok(ToResponse(result.Profile!));
    }

    private bool TryGetCustomerId(out int customerId) =>
        int.TryParse(currentUser.Subject, out customerId);

    private static CustomerProfileResponse ToResponse(CustomerProfile profile) => new(
        profile.CustomerId,
        profile.Email,
        profile.EmailVerified,
        profile.Username,
        profile.FirstName,
        profile.LastName,
        profile.Gender,
        profile.DateOfBirth,
        profile.Phone);
}

public sealed record UpdateCustomerProfileRequest(
    string? FirstName,
    string? LastName,
    string? Gender,
    DateTime? DateOfBirth,
    string? Phone);

public sealed record CustomerProfileResponse(
    int CustomerId,
    string Email,
    bool EmailVerified,
    string? Username,
    string? FirstName,
    string? LastName,
    string? Gender,
    DateTime? DateOfBirth,
    string? Phone);
