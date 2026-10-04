using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Shipping;

/// <summary>
/// Shipping rates of one shop, for members of that shop. Anyone else gets 404. The shop id comes from the route
/// and is always checked against the caller's membership; it is never taken from the body.
/// </summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/shipping-rates")]
[Authorize]
public sealed class VendorShippingRatesController(IShippingService shippingService, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int vendorId, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var rates = await shippingService.GetRatesAsync(vendorId, cancellationToken);
        return Ok(rates.Select(ShippingRateResponse.From).ToList());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int vendorId, ShippingRateRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await shippingService.CreateRateAsync(vendorId, request.ToCommand(), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(ShippingRateResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int vendorId, int id, ShippingRateRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await shippingService.UpdateRateAsync(vendorId, id, request.ToCommand(), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(ShippingRateResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await shippingService.DeleteRateAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }

    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

/// <summary>Shipping options for the signed-in customer's cart. The customer always comes from the session.</summary>
[ApiController]
[Route("api/v1/shipping")]
[Authorize]
public sealed class ShippingController(IShippingService shippingService, ICurrentUser currentUser) : ControllerBase
{
    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpPost("quote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Quote(ShippingQuoteBody request, CancellationToken cancellationToken)
    {
        var result = await shippingService.QuoteAsync(
            CustomerId, new ShippingQuoteRequest(request.AddressId, request.CountryCode, request.StateProvinceId), cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }
}

public sealed record ShippingQuoteBody(int? AddressId, string? CountryCode, int? StateProvinceId);

public sealed record ShippingRateRequest(
    string? Name, string? CountryCode, int? StateProvinceId, decimal Fee, decimal? FreeOverSubtotal,
    int? MinDays, int? MaxDays, bool Published = true, int DisplayOrder = 0)
{
    public SaveShippingRateCommand ToCommand() =>
        new(Name, CountryCode, StateProvinceId, Fee, FreeOverSubtotal, MinDays, MaxDays, Published, DisplayOrder);
}

public sealed record ShippingRateResponse(
    int Id, string Name, string CountryCode, int? StateProvinceId, decimal Fee, decimal? FreeOverSubtotal,
    int? MinDays, int? MaxDays, bool Published, int DisplayOrder)
{
    public static ShippingRateResponse From(ShippingRate rate) => new(
        rate.Id, rate.Name, rate.CountryCode, rate.StateProvinceId, rate.Fee, rate.FreeOverSubtotal,
        rate.MinDays, rate.MaxDays, rate.Published, rate.DisplayOrder);
}
