using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Vendors;

/// <summary>Internal notes about a vendor. Administrators only.</summary>
[ApiController]
[Route("api/v1/vendors/{id:int}/notes")]
[Authorize]
[HasPermission(PermissionCodes.VendorManage)]
public sealed class VendorNoteController(IVendorService vendorService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetNotes(int id,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (await vendorService.GetAsync(id, cancellationToken) is null) return NotFound();
        var result = await vendorService.GetNotesAsync(id, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), cancellationToken);
        return Ok(new VendorNotePagedResponse(
            result.Items.Select(VendorNoteResponse.From).ToList(), result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(int id, AddVendorNoteRequest request, CancellationToken cancellationToken)
    {
        if (await vendorService.GetAsync(id, cancellationToken) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Note))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["note"] = ["Note is required."] }));

        var note = await vendorService.AddNoteAsync(id, request.Note, cancellationToken);
        return Created($"/api/v1/vendors/{id}/notes", VendorNoteResponse.From(note));
    }

    [HttpDelete("{noteId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteNote(int id, int noteId, CancellationToken cancellationToken) =>
        await vendorService.DeleteNoteAsync(id, noteId, cancellationToken) ? NoContent() : NotFound();
}

public sealed record AddVendorNoteRequest(string Note);

public sealed record VendorNoteResponse(int Id, int VendorId, string Note, DateTime CreatedOnUtc)
{
    public static VendorNoteResponse From(VendorNote n) => new(n.Id, n.VendorId, n.Note, n.CreatedOnUtc);
}

public sealed record VendorNotePagedResponse(
    IReadOnlyList<VendorNoteResponse> Items, int TotalCount, int Page, int PageSize, int TotalPages);
