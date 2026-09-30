using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Catalog;

[ApiController]
[Route("api/v1/admin/catalog")]
[Authorize]
[HasPermission(PermissionCodes.CatalogManage)]
public sealed class AdminCatalogController(
    ICategoryService categoryService,
    IProductService productService,
    IManufacturerService manufacturerService,
    IAuditLogService auditLog,
    ICurrentUser currentUser) : ControllerBase
{
    private const int MaxPageSize = 100;

    // ---- Categories ----

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await categoryService.GetListAsync(new CategoryQuery(Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize)), cancellationToken);
        return Ok(ToPagedResponse(result, ToAdminCategoryResponse));
    }

    [HttpGet("categories/tree")]
    public async Task<IActionResult> GetCategoryTree(CancellationToken cancellationToken) =>
        Ok((await categoryService.GetAdminTreeAsync(cancellationToken)).Select(ToAdminTreeNode));

    [HttpGet("categories/{id:int}")]
    public async Task<IActionResult> GetCategory(int id, CancellationToken cancellationToken)
    {
        var category = await categoryService.GetAsync(id, cancellationToken);
        return category is null ? NotFound() : Ok(ToAdminCategoryResponse(category));
    }

    [HttpPost("categories")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await categoryService.CreateAsync(new CreateCategoryCommand(
            request.Name, request.Description, request.ParentCategoryId,
            request.PictureId, request.ShowOnHomepage, request.Published, request.DisplayOrder, request.RestrictFromVendors),
            cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        await AuditAsync("catalog.category_created", "Category", result.Value!.Id, null, cancellationToken);
        return CreatedAtAction(nameof(GetCategory), new { id = result.Value!.Id }, ToAdminCategoryResponse(result.Value));
    }

    [HttpPut("categories/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCategory(int id, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await categoryService.UpdateAsync(new UpdateCategoryCommand(
            id, request.Name, request.Description, request.ParentCategoryId,
            request.PictureId, request.ShowOnHomepage, request.Published, request.DisplayOrder, request.RestrictFromVendors),
            cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        await AuditAsync("catalog.category_updated", "Category", id, null, cancellationToken);
        return Ok(ToAdminCategoryResponse(result.Value!));
    }

    [HttpDelete("categories/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(int id, CancellationToken cancellationToken)
    {
        var result = await categoryService.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        await AuditAsync("catalog.category_deleted", "Category", id, null, cancellationToken);
        return NoContent();
    }

    // ---- Products ----

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] int? vendorId = null,
        [FromQuery] string? status = null,
        [FromQuery] bool? reviewRequested = null,
        CancellationToken cancellationToken = default)
    {
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

        var result = await productService.GetListAsync(
            new ProductQuery(Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), Search: search, VendorId: vendorId,
                Status: parsedStatus, ReviewRequested: reviewRequested), cancellationToken);
        return Ok(ToPagedResponse(result, ToAdminProductResponse));
    }

    [HttpGet("products/{id:int}")]
    public async Task<IActionResult> GetProduct(int id, CancellationToken cancellationToken)
    {
        var detail = await productService.GetDetailAsync(id, cancellationToken);
        if (detail is null) return NotFound();
        return Ok(new AdminProductDetailResponse(
            ToAdminProductResponse(detail.Product),
            detail.Categories.Select(c => c.Id).ToArray(),
            detail.Manufacturers.Select(m => m.Id).ToArray()));
    }

    [HttpPost("products")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProduct(SaveProductRequest request, CancellationToken cancellationToken)
    {
        var result = await productService.CreateAsync(new CreateProductCommand(
            request.Name, request.ShortDescription, request.FullDescription,
            request.Price, request.OldPrice, request.StockQuantity,
            request.Published, request.VendorId, request.ShowOnHomepage, request.DisplayOrder,
            request.CategoryIds ?? [], request.ManufacturerIds ?? []),
            ActorId(), cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        return CreatedAtAction(nameof(GetProduct), new { id = result.Value!.Id }, ToAdminProductResponse(result.Value));
    }

    [HttpPut("products/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProduct(int id, SaveProductRequest request, CancellationToken cancellationToken)
    {
        var result = await productService.UpdateAsync(new UpdateProductCommand(
            id, request.Name, request.ShortDescription, request.FullDescription,
            request.Price, request.OldPrice, request.StockQuantity,
            request.Published, request.VendorId, request.ShowOnHomepage, request.DisplayOrder,
            request.CategoryIds ?? [], request.ManufacturerIds ?? []),
            ActorId(), cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        return Ok(ToAdminProductResponse(result.Value!));
    }

    /// <summary>Hides a product from the storefront. The reason is required, is emailed to the shop and is shown to it.</summary>
    [HttpPost("products/{id:int}/hide")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> HideProduct(int id, HideProductRequest request, CancellationToken cancellationToken)
    {
        var result = await productService.HideAsync(id, request.Reason, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(ToAdminProductResponse(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>Restores the state the product had before it was hidden.</summary>
    [HttpPost("products/{id:int}/unhide")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnhideProduct(int id, CancellationToken cancellationToken)
    {
        var result = await productService.UnhideAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(ToAdminProductResponse(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>Moves a product to another shop. The only way to change a product's owner.</summary>
    [HttpPost("products/{id:int}/transfer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TransferProduct(int id, TransferProductRequest request, CancellationToken cancellationToken)
    {
        var result = await productService.TransferAsync(id, request.VendorId, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(ToAdminProductResponse(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("products/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteProduct(int id, CancellationToken cancellationToken)
    {
        var result = await productService.DeleteAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }

    // ---- Manufacturers ----

    [HttpGet("manufacturers")]
    public async Task<IActionResult> GetManufacturers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await manufacturerService.GetListAsync(new ManufacturerQuery(Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize)), cancellationToken);
        return Ok(ToPagedResponse(result, ToAdminManufacturerResponse));
    }

    [HttpGet("manufacturers/{id:int}")]
    public async Task<IActionResult> GetManufacturer(int id, CancellationToken cancellationToken)
    {
        var manufacturer = await manufacturerService.GetAsync(id, cancellationToken);
        return manufacturer is null ? NotFound() : Ok(ToAdminManufacturerResponse(manufacturer));
    }

    [HttpPost("manufacturers")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateManufacturer(SaveManufacturerRequest request, CancellationToken cancellationToken)
    {
        var result = await manufacturerService.CreateAsync(new CreateManufacturerCommand(
            request.Name, request.Description, request.PictureId, request.Published, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        await AuditAsync("catalog.manufacturer_created", "Manufacturer", result.Value!.Id, null, cancellationToken);
        return CreatedAtAction(nameof(GetManufacturer), new { id = result.Value!.Id }, ToAdminManufacturerResponse(result.Value));
    }

    [HttpPut("manufacturers/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateManufacturer(int id, SaveManufacturerRequest request, CancellationToken cancellationToken)
    {
        var result = await manufacturerService.UpdateAsync(new UpdateManufacturerCommand(
            id, request.Name, request.Description, request.PictureId, request.Published, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        await AuditAsync("catalog.manufacturer_updated", "Manufacturer", id, null, cancellationToken);
        return Ok(ToAdminManufacturerResponse(result.Value!));
    }

    [HttpDelete("manufacturers/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteManufacturer(int id, CancellationToken cancellationToken)
    {
        var result = await manufacturerService.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        await AuditAsync("catalog.manufacturer_deleted", "Manufacturer", id, null, cancellationToken);
        return NoContent();
    }

    // ---- Helpers ----

    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    // Audit entries carry ids only, never names or descriptions.
    private Task AuditAsync(string eventName, string entityType, int entityId, object? details, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(eventName, int.TryParse(currentUser.Subject, out var customerId) ? customerId : null,
            entityType: entityType, entityId: entityId, details: details, cancellationToken: cancellationToken);

    private static AdminCategoryTreeNodeResponse ToAdminTreeNode(CategoryTreeNode n) =>
        new(n.Id, n.Name, n.ParentCategoryId, n.DisplayOrder, n.Published, n.RestrictFromVendors,
            n.Children.Select(ToAdminTreeNode).ToList());

    // ---- Mapping helpers ----

    private static CatalogPagedResponse<T> ToPagedResponse<TSource, T>(PagedResult<TSource> result, Func<TSource, T> mapper) =>
        new(result.Items.Select(mapper).ToList(), result.TotalCount, result.Page, result.PageSize, result.TotalPages);

    private static AdminCategoryResponse ToAdminCategoryResponse(Category c) =>
        new(c.Id, c.Name, c.Description, c.ParentCategoryId, c.PictureId, c.ShowOnHomepage, c.Published, c.RestrictFromVendors, c.DisplayOrder, c.CreatedOnUtc, c.UpdatedOnUtc);

    private static AdminProductResponse ToAdminProductResponse(Product p) =>
        new(p.Id, p.Name, p.ShortDescription, p.FullDescription, p.Price, p.OldPrice, p.StockQuantity, p.Published, p.VendorId, p.VendorName, p.ShowOnHomepage, p.DisplayOrder, p.CreatedOnUtc, p.UpdatedOnUtc,
            ProductStatusNames.ToName(p.Status), p.HiddenReason, p.HiddenOnUtc, p.ReviewRequestedOnUtc);

    private static AdminManufacturerResponse ToAdminManufacturerResponse(Manufacturer m) =>
        new(m.Id, m.Name, m.Description, m.PictureId, m.Published, m.DisplayOrder, m.CreatedOnUtc, m.UpdatedOnUtc);
}

// Request DTOs
public sealed record SaveCategoryRequest(
    string Name,
    string? Description = null,
    int ParentCategoryId = 0,
    int PictureId = 0,
    bool ShowOnHomepage = false,
    bool Published = true,
    int DisplayOrder = 0,
    bool RestrictFromVendors = false);

public sealed record SaveProductRequest(
    string Name,
    string? ShortDescription = null,
    string? FullDescription = null,
    decimal Price = 0,
    decimal OldPrice = 0,
    int StockQuantity = 0,
    bool Published = true,
    int? VendorId = null,
    bool ShowOnHomepage = false,
    int DisplayOrder = 0,
    int[]? CategoryIds = null,
    int[]? ManufacturerIds = null);

public sealed record SaveManufacturerRequest(
    string Name,
    string? Description = null,
    int PictureId = 0,
    bool Published = true,
    int DisplayOrder = 0);

// Admin response DTOs
public sealed record AdminCategoryResponse(
    int Id, string Name, string? Description,
    int ParentCategoryId, int PictureId,
    bool ShowOnHomepage, bool Published, bool RestrictFromVendors, int DisplayOrder,
    DateTime CreatedOnUtc, DateTime UpdatedOnUtc);

public sealed record AdminCategoryTreeNodeResponse(
    int Id, string Name, int ParentCategoryId, int DisplayOrder, bool Published, bool RestrictFromVendors,
    IReadOnlyList<AdminCategoryTreeNodeResponse> Children);

public sealed record AdminProductResponse(
    int Id, string Name, string? ShortDescription, string? FullDescription,
    decimal Price, decimal OldPrice, int StockQuantity,
    bool Published, int VendorId, string? VendorName, bool ShowOnHomepage, int DisplayOrder,
    DateTime CreatedOnUtc, DateTime UpdatedOnUtc,
    string Status, string? HiddenReason, DateTime? HiddenOnUtc, DateTime? ReviewRequestedOnUtc);

public sealed record TransferProductRequest(int VendorId);

public sealed record HideProductRequest(string? Reason);

public sealed record AdminProductDetailResponse(
    AdminProductResponse Product,
    int[] CategoryIds,
    int[] ManufacturerIds);

public sealed record AdminManufacturerResponse(
    int Id, string Name, string? Description,
    int PictureId, bool Published, int DisplayOrder,
    DateTime CreatedOnUtc, DateTime UpdatedOnUtc);
