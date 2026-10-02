using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Api.Modules.Catalog;

[ApiController]
[Route("api/v1/catalog")]
[AllowAnonymous]
public sealed class CatalogController(
    ICategoryService categoryService,
    IProductService productService,
    IInventoryService inventoryService,
    IManufacturerService manufacturerService) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? parentId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await categoryService.GetListAsync(new CategoryQuery(Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), parentId, Published: true), cancellationToken);
        return Ok(ToPagedResponse(result, ToCategoryResponse));
    }

    [HttpGet("categories/tree")]
    public async Task<IActionResult> GetCategoryTree(CancellationToken cancellationToken)
    {
        var tree = await categoryService.GetTreeAsync(cancellationToken);
        return Ok(tree.Select(ToTreeNodeResponse));
    }

    [HttpGet("categories/{id:int}")]
    public async Task<IActionResult> GetCategory(int id, CancellationToken cancellationToken)
    {
        // Hidden when the category or any of its ancestors is unpublished.
        var category = await categoryService.GetPublicAsync(id, cancellationToken);
        if (category is null) return NotFound();
        return Ok(ToCategoryResponse(category));
    }

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts([FromQuery] ProductListRequest request, CancellationToken cancellationToken)
    {
        var query = new ProductQuery(
            Math.Max(request.Page, 1), Math.Clamp(request.PageSize, 1, MaxPageSize),
            request.CategoryId, request.ManufacturerId,
            request.MinPrice, request.MaxPrice,
            request.Search, request.Sort,
            Published: true, VendorId: request.VendorId, OnlyActiveShops: true);
        var result = await productService.GetListAsync(query, cancellationToken);
        var mainPictures = await productService.GetMainPictureIdsAsync(result.Items.Select(p => p.Id).ToList(), cancellationToken);
        return Ok(ToPagedResponse(result, p => ToProductResponse(p, mainPictures.GetValueOrDefault(p.Id))));
    }

    [HttpGet("products/{id:int}")]
    public async Task<IActionResult> GetProduct(int id, CancellationToken cancellationToken)
    {
        var detail = await productService.GetDetailAsync(id, cancellationToken);
        // Products of deactivated or deleted shops are not public.
        // The publication window is part of "public", same as in the list query.
        if (detail is null || !detail.Product.Published || !detail.Product.VendorActive || !detail.Product.IsAvailableAt(DateTime.UtcNow)) return NotFound();

        var availability = await inventoryService.GetAvailabilityAsync(id, cancellationToken);
        var related = await productService.GetVisibleRelatedAsync(id, cancellationToken);
        var relatedPictures = await productService.GetMainPictureIdsAsync(related.Select(p => p.Id).ToList(), cancellationToken);
        return Ok(new ProductDetailResponse(
            ToProductResponse(detail.Product, (detail.PictureIds.Count > 0 ? detail.PictureIds[0] : 0)),
            detail.Product.FullDescription,
            detail.Categories.Select(ToCategoryResponse).ToList(),
            detail.Manufacturers.Select(ToManufacturerResponse).ToList(),
            detail.PictureIds,
            related.Select(p => ToProductResponse(p, relatedPictures.GetValueOrDefault(p.Id))).ToList(),
            availability?.TrackInventory ?? true,
            availability?.Product ?? detail.Product.StockQuantity));
    }

    [HttpGet("manufacturers")]
    public async Task<IActionResult> GetManufacturers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await manufacturerService.GetListAsync(new ManufacturerQuery(Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), Published: true), cancellationToken);
        return Ok(ToPagedResponse(result, ToManufacturerResponse));
    }

    [HttpGet("manufacturers/{id:int}")]
    public async Task<IActionResult> GetManufacturer(int id, CancellationToken cancellationToken)
    {
        var manufacturer = await manufacturerService.GetAsync(id, cancellationToken);
        if (manufacturer is null || !manufacturer.Published) return NotFound();
        return Ok(ToManufacturerResponse(manufacturer));
    }

    private static CatalogPagedResponse<T> ToPagedResponse<TSource, T>(PagedResult<TSource> result, Func<TSource, T> mapper) =>
        new(result.Items.Select(mapper).ToList(), result.TotalCount, result.Page, result.PageSize, result.TotalPages);

    private static CategoryResponse ToCategoryResponse(Category c) =>
        new(c.Id, c.Name, c.Description, c.ParentCategoryId, c.PictureId, c.ShowOnHomepage, c.DisplayOrder);

    private static CategoryTreeNodeResponse ToTreeNodeResponse(CategoryTreeNode node) =>
        new(node.Id, node.Name, node.ParentCategoryId, node.DisplayOrder, node.Children.Select(ToTreeNodeResponse).ToList());

    private static ProductResponse ToProductResponse(Product p, int mainPictureId) =>
        new(p.Id, p.Name, p.ShortDescription, p.Price, p.OldPrice, p.StockQuantity, p.ShowOnHomepage, p.DisplayOrder, p.CreatedOnUtc, p.VendorId, p.VendorName, mainPictureId);

    private static ManufacturerResponse ToManufacturerResponse(Manufacturer m) =>
        new(m.Id, m.Name, m.Description, m.PictureId, m.DisplayOrder);
}

public sealed record ProductListRequest(
    int Page = 1, int PageSize = 20,
    int? CategoryId = null, int? ManufacturerId = null,
    decimal? MinPrice = null, decimal? MaxPrice = null,
    string? Search = null,
    ProductSortOrder Sort = ProductSortOrder.DisplayOrder,
    int? VendorId = null);

public sealed record CatalogPagedResponse<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public sealed record CategoryResponse(
    int Id, string Name, string? Description,
    int ParentCategoryId, int PictureId,
    bool ShowOnHomepage, int DisplayOrder);

public sealed record CategoryTreeNodeResponse(
    int Id, string Name, int ParentCategoryId, int DisplayOrder,
    IReadOnlyList<CategoryTreeNodeResponse> Children);

public sealed record ProductResponse(
    int Id, string Name, string? ShortDescription,
    decimal Price, decimal OldPrice, int StockQuantity,
    bool ShowOnHomepage, int DisplayOrder, DateTime CreatedOnUtc,
    int VendorId, string? VendorName, int MainPictureId);

public sealed record ProductDetailResponse(
    ProductResponse Product, string? FullDescription,
    IReadOnlyList<CategoryResponse> Categories,
    IReadOnlyList<ManufacturerResponse> Manufacturers,
    IReadOnlyList<int> PictureIds,
    IReadOnlyList<ProductResponse> RelatedProducts,
    bool TrackInventory,
    /// <summary>What a customer can still buy: on hand minus active reservations. Meaningless when <see cref="TrackInventory"/> is false.</summary>
    int AvailableQuantity);

public sealed record ManufacturerResponse(
    int Id, string Name, string? Description,
    int PictureId, int DisplayOrder);
