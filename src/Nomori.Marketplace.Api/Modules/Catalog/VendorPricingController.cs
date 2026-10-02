using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Catalog;

/// <summary>Special price and tier prices of the products of one shop, for members of that shop. Anyone else gets 404.</summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/products/{id:int}/pricing")]
[Authorize]
public sealed class VendorPricingController(
    IProductPricingService pricingService,
    IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetPricing(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var result = await pricingService.GetForVendorAsync(vendorId, id, cancellationToken);
        return result.Succeeded ? Ok(PricingResponse.From(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>Replaces the special price, its window and all tier prices in one call.</summary>
    [HttpPut]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPricing(int vendorId, int id, SavePricingRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await pricingService.SetForVendorAsync(vendorId, id, request.ToCommand(), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(PricingResponse.From(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>The caller, only when they are a member of the shop in the route. The shop id is never taken from the body.</summary>
    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

public sealed record TierPriceRequest(int Quantity, decimal Price);

public sealed record SavePricingRequest(
    decimal? SpecialPrice = null, DateTime? SpecialPriceStartUtc = null, DateTime? SpecialPriceEndUtc = null, TierPriceRequest[]? TierPrices = null)
{
    public SavePricingCommand ToCommand() => new(
        SpecialPrice, SpecialPriceStartUtc, SpecialPriceEndUtc, (TierPrices ?? []).Select(t => new TierPrice(t.Quantity, t.Price)).ToList());
}

public sealed record TierPriceResponse(int Quantity, decimal Price);

public sealed record PricingResponse(
    decimal? SpecialPrice, DateTime? SpecialPriceStartUtc, DateTime? SpecialPriceEndUtc, IReadOnlyList<TierPriceResponse> TierPrices)
{
    public static PricingResponse From(ProductPricing pricing) => new(
        pricing.SpecialPrice, AsUtc(pricing.SpecialPriceStartUtc), AsUtc(pricing.SpecialPriceEndUtc),
        pricing.TierPrices.Select(t => new TierPriceResponse(t.Quantity, t.Price)).ToList());

    // Values read from SQL have no kind; mark them UTC so JSON carries a trailing Z.
    public static DateTime? AsUtc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}
