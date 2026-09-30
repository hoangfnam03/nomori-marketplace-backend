using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Catalog;

/// <summary>
/// Products of one shop, for members of that shop. Anyone else (other shops, customers, administrators)
/// gets 404, so the route never reveals whether a shop or product exists. Administrators use the admin catalog API.
/// </summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/products")]
[Authorize]
public sealed class VendorProductController(
    IProductService productService,
    IVendorAccessContext accessContext) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet]
    public async Task<IActionResult> GetProducts(
        int vendorId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] bool? published = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();

        var query = new ProductQuery(Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), Search: search, Published: published);
        var result = await productService.GetListForVendorAsync(vendorId, query, cancellationToken);
        return Ok(new CatalogPagedResponse<VendorProductResponse>(
            result.Items.Select(p => VendorProductResponse.From(p, null, null)).ToList(),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetProduct(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();

        var detail = await productService.GetDetailForVendorAsync(vendorId, id, cancellationToken);
        return detail is null
            ? NotFound()
            : Ok(VendorProductResponse.From(detail.Product, detail.Categories.Select(c => c.Id).ToArray(), detail.Manufacturers.Select(m => m.Id).ToArray()));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProduct(int vendorId, SaveVendorProductRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();

        var result = await productService.CreateForVendorAsync(vendorId, request.ToCommand(), caller.CustomerId!.Value, cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);

        return CreatedAtAction(nameof(GetProduct), new { vendorId, id = result.Value!.Id },
            VendorProductResponse.From(result.Value, request.CategoryIds, request.ManufacturerIds));
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProduct(int vendorId, int id, SaveVendorProductRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();

        var result = await productService.UpdateForVendorAsync(vendorId, id, request.ToCommand(), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded
            ? Ok(VendorProductResponse.From(result.Value!, request.CategoryIds, request.ManufacturerIds))
            : this.ToFailure(result);
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteProduct(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();

        var result = await productService.DeleteForVendorAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }

    /// <summary>The caller, only when they are a member of the shop in the route. The shop id is never taken from the body.</summary>
    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

public sealed record SaveVendorProductRequest(
    string Name,
    string? ShortDescription = null,
    string? FullDescription = null,
    decimal Price = 0,
    decimal OldPrice = 0,
    int StockQuantity = 0,
    bool Published = false,
    int[]? CategoryIds = null,
    int[]? ManufacturerIds = null)
{
    public SaveVendorProductCommand ToCommand() =>
        new(Name, ShortDescription, FullDescription, Price, OldPrice, StockQuantity, Published, CategoryIds ?? [], ManufacturerIds ?? []);
}

/// <summary>Seller view of a product. Has no homepage or ordering fields because sellers do not control them.</summary>
public sealed record VendorProductResponse(
    int Id, int VendorId, string Name, string? ShortDescription, string? FullDescription,
    decimal Price, decimal OldPrice, int StockQuantity, bool Published,
    int[]? CategoryIds, int[]? ManufacturerIds, DateTime CreatedOnUtc, DateTime UpdatedOnUtc)
{
    public static VendorProductResponse From(Product p, int[]? categoryIds, int[]? manufacturerIds) => new(
        p.Id, p.VendorId, p.Name, p.ShortDescription, p.FullDescription, p.Price, p.OldPrice, p.StockQuantity, p.Published,
        categoryIds, manufacturerIds, p.CreatedOnUtc, p.UpdatedOnUtc);
}
