using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Vendors;

/// <summary>
/// Shop accounts. Members of the shop manage each other; a caller who is neither a member nor an administrator gets 404.
/// </summary>
[ApiController]
[Route("api/v1/vendors/{id:int}/members")]
[Authorize]
public sealed class VendorMemberController(
    IVendorMemberService memberService,
    IVendorAccessContext accessContext,
    IWebHostEnvironment environment,
    IOptions<EmailOptions> emailOptions) : ControllerBase
{
    private const string ConflictTitle = "Vendor member operation failed";

    [HttpGet]
    public async Task<IActionResult> GetMembers(int id, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await memberService.ListAsync(id, caller, cancellationToken);
        return result.Succeeded
            ? Ok(result.Value!.Select(m => VendorMemberResponse.From(m, caller)).ToList())
            : this.ToFailure(result, ConflictTitle);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateMember(int id, CreateVendorMemberRequest request, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await memberService.CreateAsync(
            id, new CreateVendorMemberCommand(request.Email, request.FirstName, request.LastName), caller, cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result, ConflictTitle);

        var response = VendorMemberCreatedResponse.From(
            VendorMemberResponse.From(result.Value!.Member, caller), DevelopmentToken(result.Value.SetupToken));
        return Created($"/api/v1/vendors/{id}/members", response);
    }

    [HttpPost("{customerId:int}/setup-email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendSetupEmail(int id, int customerId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await memberService.ResendSetupEmailAsync(id, customerId, caller, cancellationToken);
        return result.Succeeded
            ? Ok(new { developmentSetupToken = DevelopmentToken(result.Value!) })
            : this.ToFailure(result, ConflictTitle);
    }

    [HttpDelete("{customerId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(int id, int customerId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        var result = await memberService.RemoveAsync(id, customerId, caller, cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result, ConflictTitle);
    }

    // The raw token is only ever returned in Development with email delivery off, so the flow can be tested locally.
    private string? DevelopmentToken(string token) =>
        environment.IsDevelopment() && !emailOptions.Value.Enabled ? token : null;
}

public sealed record CreateVendorMemberRequest(string Email, string? FirstName = null, string? LastName = null);

public sealed record VendorMemberResponse(
    int CustomerId, string Email, string? FirstName, string? LastName,
    string Status, bool IsCurrentUser, DateTime CreatedOnUtc, DateTime? LastLoginDateUtc)
{
    public static VendorMemberResponse From(VendorMember m, VendorCaller caller) => new(
        m.CustomerId, m.Email, m.FirstName, m.LastName,
        VendorApplicationResponse.ToCamelCase(m.Status.ToString()),
        caller.CustomerId == m.CustomerId, m.CreatedOnUtc, m.LastLoginDateUtc);
}

public sealed record VendorMemberCreatedResponse(
    int CustomerId, string Email, string? FirstName, string? LastName,
    string Status, bool IsCurrentUser, DateTime CreatedOnUtc, DateTime? LastLoginDateUtc,
    string? DevelopmentSetupToken)
{
    public static VendorMemberCreatedResponse From(VendorMemberResponse m, string? developmentSetupToken) => new(
        m.CustomerId, m.Email, m.FirstName, m.LastName, m.Status, m.IsCurrentUser, m.CreatedOnUtc, m.LastLoginDateUtc,
        developmentSetupToken);
}
