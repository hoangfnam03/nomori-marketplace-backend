using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Vendors;

// ---- Public storefront ----

[ApiController]
[Route("api/v1/vendors")]
[AllowAnonymous]
public sealed class VendorController(IVendorService vendorService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetVendors(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await vendorService.GetListAsync(new VendorQuery(page, pageSize, search, Active: true), cancellationToken);
        return Ok(new VendorPublicPagedResponse(result.Items.Select(ToPublicResponse).ToList(), result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetVendor(int id, CancellationToken cancellationToken)
    {
        var vendor = await vendorService.GetAsync(id, cancellationToken);
        if (vendor is null || !vendor.Active) return NotFound();
        return Ok(ToPublicResponse(vendor));
    }

    private static VendorPublicResponse ToPublicResponse(Vendor v) =>
        new(v.Id, v.Name, v.Email, v.Description, v.PictureId, v.DisplayOrder);
}

// ---- Vendor portal (own vendor info) ----

[ApiController]
[Route("api/v1/vendor/portal")]
[Authorize]
[HasPermission(PermissionCodes.VendorPortal)]
public sealed class VendorPortalController(IVendorService vendorService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetMyVendor(CancellationToken cancellationToken)
    {
        var customerIdClaim = User.FindFirst("sub")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(customerIdClaim, out var customerId)) return Unauthorized();
        var vendor = await vendorService.GetCurrentVendorAsync(customerId, cancellationToken);
        if (vendor is null) return NotFound();
        return Ok(ToAdminResponse(vendor));
    }

    private static VendorAdminResponse ToAdminResponse(Vendor v) =>
        new(v.Id, v.Name, v.Email, v.Description, v.PictureId, v.AddressId, v.Active, v.DisplayOrder, v.CreatedOnUtc, v.UpdatedOnUtc);
}

// ---- Admin ----

[ApiController]
[Route("api/v1/admin/vendors")]
[Authorize]
[HasPermission(PermissionCodes.VendorManage)]
public sealed class AdminVendorController(IVendorService vendorService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetVendors(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] bool? active = null,
        CancellationToken cancellationToken = default)
    {
        var result = await vendorService.GetListAsync(new VendorQuery(page, pageSize, search, active), cancellationToken);
        return Ok(new VendorAdminPagedResponse(result.Items.Select(ToAdminResponse).ToList(), result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetVendor(int id, CancellationToken cancellationToken)
    {
        var vendor = await vendorService.GetAsync(id, cancellationToken);
        return vendor is null ? NotFound() : Ok(ToAdminResponse(vendor));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateVendor(SaveVendorRequest request, CancellationToken cancellationToken)
    {
        var result = await vendorService.CreateAsync(
            new CreateVendorCommand(request.Name, request.Email, request.Description, request.AdminComment, request.Active, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return CreatedAtAction(nameof(GetVendor), new { id = result.Value!.Id }, ToAdminResponse(result.Value));
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateVendor(int id, SaveVendorRequest request, CancellationToken cancellationToken)
    {
        var result = await vendorService.UpdateAsync(
            new UpdateVendorCommand(id, request.Name, request.Email, request.Description, request.AdminComment, request.Active, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(ToAdminResponse(result.Value!));
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteVendor(int id, CancellationToken cancellationToken) =>
        await vendorService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    // ---- Customer assignment ----

    [HttpPost("{id:int}/customer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignCustomer(int id, AssignCustomerRequest request, CancellationToken cancellationToken) =>
        await vendorService.AssignCustomerAsync(id, request.CustomerId, cancellationToken) ? NoContent() : NotFound();

    [HttpDelete("{id:int}/customer/{customerId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnassignCustomer(int id, int customerId, CancellationToken cancellationToken)
    {
        await vendorService.UnassignCustomerAsync(customerId, cancellationToken);
        return NoContent();
    }

    // ---- Notes ----

    [HttpGet("{id:int}/notes")]
    public async Task<IActionResult> GetNotes(int id,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var vendor = await vendorService.GetAsync(id, cancellationToken);
        if (vendor is null) return NotFound();
        var result = await vendorService.GetNotesAsync(id, page, pageSize, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:int}/notes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(int id, AddVendorNoteRequest request, CancellationToken cancellationToken)
    {
        var vendor = await vendorService.GetAsync(id, cancellationToken);
        if (vendor is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Note))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["note"] = ["Note is required."] }));
        var note = await vendorService.AddNoteAsync(id, request.Note, cancellationToken);
        return Ok(note);
    }

    [HttpDelete("{id:int}/notes/{noteId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteNote(int id, int noteId, CancellationToken cancellationToken) =>
        await vendorService.DeleteNoteAsync(noteId, cancellationToken) ? NoContent() : NotFound();

    private static VendorAdminResponse ToAdminResponse(Vendor v) =>
        new(v.Id, v.Name, v.Email, v.Description, v.PictureId, v.AddressId, v.Active, v.DisplayOrder, v.CreatedOnUtc, v.UpdatedOnUtc);
}

// ---- DTOs ----

public sealed record VendorPublicPagedResponse(
    IReadOnlyList<VendorPublicResponse> Items,
    int TotalCount, int Page, int PageSize, int TotalPages);

public sealed record VendorAdminPagedResponse(
    IReadOnlyList<VendorAdminResponse> Items,
    int TotalCount, int Page, int PageSize, int TotalPages);

public sealed record VendorPublicResponse(
    int Id, string Name, string Email, string? Description,
    int PictureId, int DisplayOrder);

public sealed record VendorAdminResponse(
    int Id, string Name, string Email, string? Description,
    int PictureId, int AddressId, bool Active, int DisplayOrder,
    DateTime CreatedOnUtc, DateTime UpdatedOnUtc);

public sealed record SaveVendorRequest(
    string Name,
    string Email,
    string? Description = null,
    string? AdminComment = null,
    bool Active = true,
    int DisplayOrder = 0);

public sealed record AssignCustomerRequest(int CustomerId);

public sealed record AddVendorNoteRequest(string Note);
