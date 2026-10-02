using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Catalog;

/// <summary>
/// Variants, specifications and tags of the products of one shop, for members of that shop. Anyone else gets 404.
/// The definitions (attributes, specification attributes) stay platform-owned: sellers only choose from them.
/// </summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}")]
[Authorize]
public sealed class VendorProductDetailsController(
    IVendorProductDetailsService service,
    IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet("product-options")]
    public async Task<IActionResult> GetOptions(int vendorId, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();

        var catalog = await service.GetOptionCatalogAsync(cancellationToken);
        return Ok(new
        {
            attributes = catalog.Attributes.Select(a => new { a.Id, a.Name }),
            specAttributes = catalog.SpecAttributes.Select(s => new
            {
                s.Attribute.Id,
                s.Attribute.Name,
                s.GroupName,
                options = s.Options.Select(o => new { o.Id, o.Name })
            })
        });
    }

    [HttpGet("products/{id:int}/tags")]
    public async Task<IActionResult> GetTags(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var result = await service.GetTagsAsync(vendorId, id, cancellationToken);
        return result.Succeeded ? Ok(new { tagNames = result.Value }) : this.ToFailure(result);
    }

    [HttpPut("products/{id:int}/tags")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTags(int vendorId, int id, SetProductTagNamesRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await service.SetTagsAsync(vendorId, id, request.TagNames, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(new { tagNames = result.Value }) : this.ToFailure(result);
    }

    [HttpGet("products/{id:int}/specs")]
    public async Task<IActionResult> GetSpecs(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var result = await service.GetSpecOptionIdsAsync(vendorId, id, cancellationToken);
        return result.Succeeded ? Ok(new { optionIds = result.Value }) : this.ToFailure(result);
    }

    [HttpPut("products/{id:int}/specs")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSpecs(int vendorId, int id, SetSpecOptionsRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await service.SetSpecOptionsAsync(vendorId, id, request.OptionIds, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(new { optionIds = result.Value }) : this.ToFailure(result);
    }

    [HttpGet("products/{id:int}/variants")]
    public async Task<IActionResult> GetVariants(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var result = await service.GetVariantsAsync(vendorId, id, cancellationToken);
        return result.Succeeded ? Ok(VariantsResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("products/{id:int}/variants")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetVariants(int vendorId, int id, SaveVariantsRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await service.SetVariantsAsync(vendorId, id, request.ToCommand(), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(VariantsResponse.From(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>The caller, only when they are a member of the shop in the route. The shop id is never taken from the body.</summary>
    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

public sealed record SetProductTagNamesRequest(string[]? TagNames);

public sealed record SetSpecOptionsRequest(int[]? OptionIds);

public sealed record VariantValueRequest(string? Name, string? ColorSquaresRgb = null, decimal PriceAdjustment = 0);

public sealed record VariantAttributeRequest(int ProductAttributeId, bool IsRequired = false, VariantValueRequest[]? Values = null);

public sealed record VariantCombinationRequest(int[]? ValueIndexes, string? Sku = null, int StockQuantity = 0, decimal? OverriddenPrice = null);

public sealed record SaveVariantsRequest(VariantAttributeRequest[]? Attributes = null, VariantCombinationRequest[]? Combinations = null)
{
    public SaveVariantsCommand ToCommand() => new(
        (Attributes ?? []).Select(a => new VariantAttributeInput(
            a.ProductAttributeId, a.IsRequired,
            (a.Values ?? []).Select(v => new VariantValueInput(v.Name ?? string.Empty, v.ColorSquaresRgb, v.PriceAdjustment)).ToList())).ToList(),
        (Combinations ?? []).Select(c => new VariantCombinationInput(c.ValueIndexes ?? [], c.Sku, c.StockQuantity, c.OverriddenPrice)).ToList());
}

/// <summary>Same shape as the public attributes response, so one client model reads both.</summary>
public sealed record VariantsResponse(object Mappings, object Combinations)
{
    public static VariantsResponse From(ProductAttributeDetail d) => new(
        d.Mappings.Select(m => new
        {
            m.Mapping.Id,
            m.Mapping.IsRequired,
            m.Mapping.DisplayOrder,
            controlType = m.Mapping.ControlType.ToString(),
            attribute = new { m.Attribute.Id, m.Attribute.Name },
            values = m.Values.Select(v => new { v.Id, v.Name, v.ColorSquaresRgb, v.PriceAdjustment, v.DisplayOrder })
        }).ToList(),
        d.Combinations.Select(c => new { c.Id, c.AttributesJson, c.StockQuantity, c.Sku, c.OverriddenPrice }).ToList());
}
