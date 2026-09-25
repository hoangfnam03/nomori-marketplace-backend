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
    IManufacturerService manufacturerService) : ControllerBase
{
    // ---- Categories ----

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await categoryService.GetListAsync(new CategoryQuery(page, pageSize), cancellationToken);
        return Ok(ToPagedResponse(result, ToAdminCategoryResponse));
    }

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
            request.PictureId, request.ShowOnHomepage, request.Published, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return CreatedAtAction(nameof(GetCategory), new { id = result.Value!.Id }, ToAdminCategoryResponse(result.Value));
    }

    [HttpPut("categories/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCategory(int id, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await categoryService.UpdateAsync(new UpdateCategoryCommand(
            id, request.Name, request.Description, request.ParentCategoryId,
            request.PictureId, request.ShowOnHomepage, request.Published, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(ToAdminCategoryResponse(result.Value!));
    }

    [HttpDelete("categories/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(int id, CancellationToken cancellationToken) =>
        await categoryService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    // ---- Products ----

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await productService.GetListAsync(new ProductQuery(page, pageSize, Search: search), cancellationToken);
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
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
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
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(ToAdminProductResponse(result.Value!));
    }

    [HttpDelete("products/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteProduct(int id, CancellationToken cancellationToken) =>
        await productService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    // ---- Manufacturers ----

    [HttpGet("manufacturers")]
    public async Task<IActionResult> GetManufacturers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await manufacturerService.GetListAsync(new ManufacturerQuery(page, pageSize), cancellationToken);
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
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return CreatedAtAction(nameof(GetManufacturer), new { id = result.Value!.Id }, ToAdminManufacturerResponse(result.Value));
    }

    [HttpPut("manufacturers/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateManufacturer(int id, SaveManufacturerRequest request, CancellationToken cancellationToken)
    {
        var result = await manufacturerService.UpdateAsync(new UpdateManufacturerCommand(
            id, request.Name, request.Description, request.PictureId, request.Published, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(ToAdminManufacturerResponse(result.Value!));
    }

    [HttpDelete("manufacturers/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteManufacturer(int id, CancellationToken cancellationToken) =>
        await manufacturerService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    // ---- Mapping helpers ----

    private static CatalogPagedResponse<T> ToPagedResponse<TSource, T>(PagedResult<TSource> result, Func<TSource, T> mapper) =>
        new(result.Items.Select(mapper).ToList(), result.TotalCount, result.Page, result.PageSize, result.TotalPages);

    private static AdminCategoryResponse ToAdminCategoryResponse(Category c) =>
        new(c.Id, c.Name, c.Description, c.ParentCategoryId, c.PictureId, c.ShowOnHomepage, c.Published, c.DisplayOrder, c.CreatedOnUtc, c.UpdatedOnUtc);

    private static AdminProductResponse ToAdminProductResponse(Product p) =>
        new(p.Id, p.Name, p.ShortDescription, p.FullDescription, p.Price, p.OldPrice, p.StockQuantity, p.Published, p.VendorId, p.ShowOnHomepage, p.DisplayOrder, p.CreatedOnUtc, p.UpdatedOnUtc);

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
    int DisplayOrder = 0);

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
    bool ShowOnHomepage, bool Published, int DisplayOrder,
    DateTime CreatedOnUtc, DateTime UpdatedOnUtc);

public sealed record AdminProductResponse(
    int Id, string Name, string? ShortDescription, string? FullDescription,
    decimal Price, decimal OldPrice, int StockQuantity,
    bool Published, int? VendorId, bool ShowOnHomepage, int DisplayOrder,
    DateTime CreatedOnUtc, DateTime UpdatedOnUtc);

public sealed record AdminProductDetailResponse(
    AdminProductResponse Product,
    int[] CategoryIds,
    int[] ManufacturerIds);

public sealed record AdminManufacturerResponse(
    int Id, string Name, string? Description,
    int PictureId, bool Published, int DisplayOrder,
    DateTime CreatedOnUtc, DateTime UpdatedOnUtc);
