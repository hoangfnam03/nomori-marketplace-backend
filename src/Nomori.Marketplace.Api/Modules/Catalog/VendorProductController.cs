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
        [FromQuery] string? status = null,
        [FromQuery] bool? lowStock = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();

        ProductStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!ProductStatusNames.TryParse(status, out var value))
                return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    ["status"] = ["Status must be draft, live, stopped or hiddenByAdmin."]
                }));
            parsedStatus = value;
        }

        var query = new ProductQuery(Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), Search: search, Status: parsedStatus, LowStock: lowStock);
        var result = await productService.GetListForVendorAsync(vendorId, query, cancellationToken);
        var mainPictures = await productService.GetMainPictureIdsAsync(result.Items.Select(p => p.Id).ToList(), cancellationToken);
        return Ok(new CatalogPagedResponse<VendorProductResponse>(
            result.Items.Select(p => VendorProductResponse.From(p, null, null, null, mainPictures.GetValueOrDefault(p.Id))).ToList(),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetProduct(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();

        var detail = await productService.GetDetailForVendorAsync(vendorId, id, cancellationToken);
        return detail is null
            ? NotFound()
            : Ok(VendorProductResponse.From(detail.Product, detail.Categories.Select(c => c.Id).ToArray(), detail.Manufacturers.Select(m => m.Id).ToArray(),
                detail.PictureIds.ToArray(), (detail.PictureIds.Count > 0 ? detail.PictureIds[0] : 0), detail.RelatedProductIds.ToArray()));
    }

    /// <summary>Replaces the ordered related products of a product (maximum 12, products of this shop only).</summary>
    [HttpPut("{id:int}/related")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRelated(int vendorId, int id, SetRelatedProductsRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();

        var result = await productService.SetRelatedForVendorAsync(vendorId, id, request.ProductIds, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(new { relatedProductIds = result.Value }) : this.ToFailure(result);
    }

    /// <summary>Copies a product into a new draft of the same shop.</summary>
    [HttpPost("{id:int}/copy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CopyProduct(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();

        var result = await productService.CopyForVendorAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded
            ? CreatedAtAction(nameof(GetProduct), new { vendorId, id = result.Value!.Id }, VendorProductResponse.From(result.Value, null, null))
            : this.ToFailure(result);
    }

    /// <summary>Replaces the ordered pictures of a product (maximum 10; the first is the main picture).</summary>
    [HttpPut("{id:int}/pictures")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPictures(int vendorId, int id, SetProductPicturesRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();

        var result = await productService.SetPicturesForVendorAsync(vendorId, id, request.PictureIds, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(new { pictureIds = result.Value }) : this.ToFailure(result);
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

    /// <summary>Publishes or stops a product of this shop. A product hidden by an administrator cannot be changed by its shop.</summary>
    [HttpPut("{id:int}/status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int vendorId, int id, ChangeProductStatusRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        if (!ProductStatusNames.TryParse(request.Status, out var target))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["status"] = ["Status must be live or stopped."] }));

        var result = await productService.SetStatusForVendorAsync(vendorId, id, target, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(VendorProductResponse.From(result.Value!, null, null)) : this.ToFailure(result);
    }

    /// <summary>Asks an administrator to look at a hidden product again.</summary>
    [HttpPost("{id:int}/review-request")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestReview(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();

        var result = await productService.RequestReviewForVendorAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(VendorProductResponse.From(result.Value!, null, null)) : this.ToFailure(result);
    }

    /// <summary>The caller, only when they are a member of the shop in the route. The shop id is never taken from the body.</summary>
    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

public sealed record ChangeProductStatusRequest(string? Status);

public sealed record SetProductPicturesRequest(int[]? PictureIds);

public sealed record SetRelatedProductsRequest(int[]? ProductIds);

public sealed record SaveVendorProductRequest(
    string Name,
    string? ShortDescription = null,
    string? FullDescription = null,
    decimal Price = 0,
    decimal OldPrice = 0,
    int StockQuantity = 0,
    int[]? CategoryIds = null,
    int[]? ManufacturerIds = null,
    string? Sku = null,
    string? Gtin = null,
    string? ManufacturerPartNumber = null,
    DateTime? AvailableStartUtc = null,
    DateTime? AvailableEndUtc = null)
{
    public SaveVendorProductCommand ToCommand() =>
        new(Name, ShortDescription, FullDescription, Price, OldPrice, StockQuantity, CategoryIds ?? [], ManufacturerIds ?? [],
            Sku, Gtin, ManufacturerPartNumber, AvailableStartUtc, AvailableEndUtc);
}

/// <summary>Seller view of a product. Has no homepage or ordering fields because sellers do not control them.</summary>
public sealed record VendorProductResponse(
    int Id, int VendorId, string Name, string? ShortDescription, string? FullDescription,
    decimal Price, decimal OldPrice, int StockQuantity, bool Published,
    string Status, string? HiddenReason, DateTime? ReviewRequestedOnUtc,
    int[]? CategoryIds, int[]? ManufacturerIds, DateTime CreatedOnUtc, DateTime UpdatedOnUtc,
    int[]? PictureIds, int MainPictureId,
    bool TrackInventory, int LowStockThreshold, bool IsLowStock,
    string? Sku, string? Gtin, string? ManufacturerPartNumber, DateTime? AvailableStartUtc, DateTime? AvailableEndUtc,
    int[]? RelatedProductIds)
{
    /// <param name="pictureIds">Ordered picture ids; only known when a single product is read.</param>
    /// <param name="mainPictureId">First picture id, or 0 when there is none or it is not known.</param>
    public static VendorProductResponse From(
        Product p, int[]? categoryIds, int[]? manufacturerIds, int[]? pictureIds = null, int mainPictureId = 0, int[]? relatedProductIds = null) => new(
        p.Id, p.VendorId, p.Name, p.ShortDescription, p.FullDescription, p.Price, p.OldPrice, p.StockQuantity, p.Published,
        ProductStatusNames.ToName(p.Status), p.Status == ProductStatus.HiddenByAdmin ? p.HiddenReason : null, p.ReviewRequestedOnUtc,
        categoryIds, manufacturerIds, p.CreatedOnUtc, p.UpdatedOnUtc, pictureIds, mainPictureId,
        p.TrackInventory, p.LowStockThreshold, p.IsLowStock,
        p.Sku, p.Gtin, p.ManufacturerPartNumber, AsUtc(p.AvailableStartUtc), AsUtc(p.AvailableEndUtc), relatedProductIds);

    // Values read from SQL have no kind; mark them UTC so JSON carries a trailing Z.
    private static DateTime? AsUtc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}
