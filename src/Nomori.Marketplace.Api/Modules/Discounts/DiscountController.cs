using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Discounts;

/// <summary>
/// Discounts funded by one shop, for members of that shop. Anyone else gets 404. The shop comes from the route and is checked against the
/// membership on every call; it is never taken from the body, so a discount cannot be created for or moved to another shop.
/// </summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/discounts")]
[Authorize]
public sealed class VendorDiscountsController(IDiscountService discountService, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int vendorId, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        return Ok((await discountService.GetListAsync(vendorId, cancellationToken)).Select(DiscountResponse.From));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int vendorId, SaveDiscountRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await discountService.CreateAsync(vendorId, request.ToCommand(), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(DiscountResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int vendorId, int id, SaveDiscountRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await discountService.UpdateAsync(vendorId, id, request.ToCommand(), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(DiscountResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await discountService.DeleteAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }

    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

/// <summary>Platform-funded discounts, for administrators. A shop's discounts are not reachable here.</summary>
[ApiController]
[Route("api/v1/admin/discounts")]
[Authorize]
[HasPermission(PermissionCodes.DiscountsManage)]
public sealed class AdminDiscountsController(IDiscountService discountService, ICurrentUser currentUser) : ControllerBase
{
    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok((await discountService.GetListAsync(null, cancellationToken)).Select(DiscountResponse.From));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SaveDiscountRequest request, CancellationToken cancellationToken)
    {
        var result = await discountService.CreateAsync(null, request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(DiscountResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, SaveDiscountRequest request, CancellationToken cancellationToken)
    {
        var result = await discountService.UpdateAsync(null, id, request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(DiscountResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await discountService.DeleteAsync(null, id, ActorId(), cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }
}

public sealed record SaveDiscountRequest(
    string? Name, string? Code, string? Type, decimal Value, decimal? MaxDiscountAmount, DateTime? StartsOnUtc, DateTime? EndsOnUtc,
    decimal? MinSubtotal, int? MaxUses, int? MaxUsesPerCustomer, bool Enabled = true)
{
    public SaveDiscountCommand ToCommand() => new(
        Name, Code, Type, Value, MaxDiscountAmount, AsUtc(StartsOnUtc), AsUtc(EndsOnUtc), MinSubtotal, MaxUses, MaxUsesPerCustomer, Enabled);

    private static DateTime? AsUtc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}

public sealed record DiscountResponse(
    int Id, string Name, string Code, string Type, decimal Value, decimal? MaxDiscountAmount, DateTime? StartsOnUtc, DateTime? EndsOnUtc,
    decimal? MinSubtotal, int? MaxUses, int? MaxUsesPerCustomer, int UsedCount, bool Enabled, string Funding, DateTime CreatedOnUtc)
{
    public static DiscountResponse From(Discount d) => new(
        d.Id, d.Name, d.Code, DiscountRules.ToWire(d.Type), d.Value, d.MaxDiscountAmount, d.StartsOnUtc, d.EndsOnUtc, d.MinSubtotal, d.MaxUses,
        d.MaxUsesPerCustomer, d.UsedCount, d.Enabled, DiscountRules.ToWire(d.Funding), d.CreatedOnUtc);
}
