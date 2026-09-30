using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Vendors;

/// <summary>
/// One set of vendor routes for every caller. What the caller may see is decided per request from <see cref="VendorCaller"/>.
/// </summary>
[ApiController]
[Route("api/v1/vendors")]
public sealed class VendorController(IVendorService vendorService, IVendorAccessContext accessContext) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetVendors(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] bool? active = null,
        CancellationToken cancellationToken = default)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        MarkPrivateWhenSignedIn(caller);

        // Only administrators may list inactive vendors; a member also sees their own shop while it is switched off.
        var query = new VendorQuery(
            Math.Max(page, 1),
            Math.Clamp(pageSize, 1, MaxPageSize),
            NullIfBlank(search),
            caller.IsAdmin ? active : true,
            caller.IsAdmin ? null : caller.MemberVendorId);

        var result = await vendorService.GetListAsync(query, cancellationToken);
        return Ok(new VendorPagedResponse(
            result.Items.Select(v => VendorResponse.From(v, caller)).ToList(),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetVendor(int id, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        MarkPrivateWhenSignedIn(caller);

        var vendor = await vendorService.GetAsync(id, cancellationToken);
        if (vendor is null || !(vendor.Active || caller.IsAdmin || caller.IsMemberOf(id))) return NotFound();
        return Ok(VendorResponse.From(vendor, caller));
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    [Authorize]
    [HasPermission(PermissionCodes.VendorManage)]
    public async Task<IActionResult> UpdateVendor(int id, UpdateVendorRequest request, CancellationToken cancellationToken)
    {
        var result = await vendorService.UpdateAsync(
            new UpdateVendorCommand(id, request.Name, request.Email, request.Description, request.AdminComment, request.Active, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result, "Vendor update failed");

        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return Ok(VendorResponse.From(result.Value!, caller));
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    [Authorize]
    [HasPermission(PermissionCodes.VendorManage)]
    public async Task<IActionResult> DeleteVendor(int id, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return await vendorService.DeleteAsync(id, caller.CustomerId!.Value, cancellationToken) ? NoContent() : NotFound();
    }

    // Responses differ per caller, so they must never be shared through a cache.
    private void MarkPrivateWhenSignedIn(VendorCaller caller)
    {
        if (caller.IsAuthenticated) Response.Headers.CacheControl = "private, no-store";
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record UpdateVendorRequest(
    string Name,
    string Email,
    string? Description = null,
    string? AdminComment = null,
    bool Active = true,
    int DisplayOrder = 0);

public sealed record VendorPagedResponse(
    IReadOnlyList<VendorResponse> Items,
    int TotalCount, int Page, int PageSize, int TotalPages);

/// <summary>Fields other than the first six are <c>null</c> unless the caller is an administrator or a member of this vendor.</summary>
public sealed record VendorResponse(
    int Id, string Name, string Email, string? Description, int PictureId, int DisplayOrder,
    bool? Active, int? AddressId, DateTime? CreatedOnUtc, DateTime? UpdatedOnUtc, string? AdminComment)
{
    public static VendorResponse From(Vendor v, VendorCaller caller)
    {
        var privileged = caller.IsAdmin || caller.IsMemberOf(v.Id);
        return new VendorResponse(
            v.Id, v.Name, v.Email, v.Description, v.PictureId, v.DisplayOrder,
            privileged ? v.Active : null,
            privileged ? v.AddressId : null,
            privileged ? v.CreatedOnUtc : null,
            privileged ? v.UpdatedOnUtc : null,
            caller.IsAdmin ? v.AdminComment : null);
    }
}
