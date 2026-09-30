using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Vendors;

/// <summary>Shop applications. Customers see their own; administrators review all of them.</summary>
[ApiController]
[Route("api/v1/vendor-applications")]
[Authorize]
public sealed class VendorApplicationController(
    IVendorApplicationService applicationService,
    IVendorAccessContext accessContext) : ControllerBase
{
    private const string ConflictTitle = "Vendor application failed";

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(SaveVendorApplicationRequest request, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await applicationService.SubmitAsync(
            new SubmitVendorApplicationCommand(request.ShopName, request.Email, request.PhoneNumber, request.Description, request.TaxCode, request.BusinessAddress),
            caller, cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result, ConflictTitle);

        var response = VendorApplicationResponse.From(result.Value!, caller);
        return Created($"/api/v1/vendor-applications/{response.Id}", response);
    }

    [HttpGet]
    public async Task<IActionResult> GetApplications(
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        VendorApplicationStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<VendorApplicationStatus>(status, ignoreCase: true, out var value) || !Enum.IsDefined(value))
                return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    ["status"] = ["Status must be pending, approved, rejected or cancelled."]
                }));
            parsedStatus = value;
        }

        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await applicationService.GetListAsync(
            new VendorApplicationQuery(Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), parsedStatus, search),
            caller, cancellationToken);

        return Ok(new VendorApplicationPagedResponse(
            result.Items.Select(a => VendorApplicationResponse.From(a, caller)).ToList(),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetApplication(int id, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await applicationService.GetAsync(id, caller, cancellationToken);
        return result.Succeeded
            ? Ok(VendorApplicationResponse.From(result.Value!, caller))
            : this.ToFailure(result, ConflictTitle);
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, SaveVendorApplicationRequest request, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await applicationService.UpdateAsync(
            id,
            new UpdateVendorApplicationCommand(request.ShopName, request.Email, request.PhoneNumber, request.Description, request.TaxCode, request.BusinessAddress),
            caller, cancellationToken);
        return result.Succeeded
            ? Ok(VendorApplicationResponse.From(result.Value!, caller))
            : this.ToFailure(result, ConflictTitle);
    }

    [HttpPut("{id:int}/status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStatus(int id, ChangeVendorApplicationStatusRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseTargetStatus(request.Status, out var target))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["status"] = ["Status must be approved, rejected or cancelled."]
            }));

        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await applicationService.ChangeStatusAsync(
            id, new ChangeVendorApplicationStatusCommand(target, request.Reason, request.ShopName, request.AdminComment),
            caller, cancellationToken);
        return result.Succeeded
            ? Ok(VendorApplicationResponse.From(result.Value!, caller))
            : this.ToFailure(result, ConflictTitle);
    }

    private static bool TryParseTargetStatus(string? value, out VendorApplicationStatus status)
    {
        status = default;
        return Enum.TryParse(value, ignoreCase: true, out status)
            && Enum.IsDefined(status)
            && status != VendorApplicationStatus.Pending;
    }
}

public sealed record SaveVendorApplicationRequest(
    string ShopName,
    string Email,
    string PhoneNumber,
    string? Description = null,
    string? TaxCode = null,
    string? BusinessAddress = null);

public sealed record ChangeVendorApplicationStatusRequest(
    string? Status,
    string? Reason = null,
    string? ShopName = null,
    string? AdminComment = null);

public sealed record VendorApplicationPagedResponse(
    IReadOnlyList<VendorApplicationResponse> Items,
    int TotalCount, int Page, int PageSize, int TotalPages);

/// <summary>Applicant account fields are <c>null</c> unless the caller is an administrator.</summary>
public sealed record VendorApplicationResponse(
    int Id, string ShopName, string Email, string PhoneNumber, string? Description, string? TaxCode, string? BusinessAddress,
    string Status, string? RejectReason, int? VendorId,
    DateTime CreatedOnUtc, DateTime UpdatedOnUtc, DateTime? ReviewedOnUtc,
    int? CustomerId, string? CustomerEmail, string? CustomerUsername, int? ReviewedByCustomerId)
{
    public static VendorApplicationResponse From(VendorApplication a, VendorCaller caller) => new(
        a.Id, a.ShopName, a.Email, a.PhoneNumber, a.Description, a.TaxCode, a.BusinessAddress,
        ToCamelCase(a.Status.ToString()), a.RejectReason, a.VendorId,
        a.CreatedOnUtc, a.UpdatedOnUtc, a.ReviewedOnUtc,
        caller.IsAdmin ? a.CustomerId : null,
        caller.IsAdmin ? a.CustomerEmail : null,
        caller.IsAdmin ? a.CustomerUsername : null,
        caller.IsAdmin ? a.ReviewedByCustomerId : null);

    internal static string ToCamelCase(string value) => char.ToLowerInvariant(value[0]) + value[1..];
}
