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
    ICatalogSearchService searchService,
    IPriceCalculationService priceService,
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
        // Validation, category descendants and every public rule live in the search service.
        var search = await searchService.SearchAsync(request.ToSearch(), cancellationToken);
        if (!search.Succeeded) return this.ToFailure(search);

        var result = search.Value!;
        var mainPictures = await productService.GetMainPictureIdsAsync(result.Items.Select(p => p.Id).ToList(), cancellationToken);
        return Ok(ToPagedResponse(result, p => ToProductResponse(p, mainPictures.GetValueOrDefault(p.Id))));
    }

    /// <summary>Counts for the filter panel, for the same query string as the list (paging and sort are ignored).</summary>
    [HttpGet("products/facets")]
    public async Task<IActionResult> GetFacets([FromQuery] ProductListRequest request, CancellationToken cancellationToken)
    {
        var result = await searchService.GetFacetsAsync(request.ToSearch(), cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }

    /// <summary>Up to 8 visible products for the text typed so far; nothing for fewer than 2 characters.</summary>
    [HttpGet("products/suggest")]
    public async Task<IActionResult> Suggest([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var result = await searchService.SuggestAsync(q, cancellationToken);
        var items = result.Value ?? [];
        var pictures = await productService.GetMainPictureIdsAsync(items.Select(p => p.Id).ToList(), cancellationToken);
        return Ok(items.Select(p => new ProductSuggestionResponse(p.Id, p.Name, p.Price, pictures.GetValueOrDefault(p.Id))));
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
            availability?.Product ?? detail.Product.StockQuantity,
            detail.TierPrices.Select(t => new TierPriceResponse(t.Quantity, t.Price)).ToList()));
    }

    /// <summary>
    /// The price of a quantity of a product with the chosen variant values (<c>valueIds</c>, repeat the key), in the primary currency.
    /// Special price, tier price and variant rules are applied by the server; screens only show the answer.
    /// </summary>
    [HttpGet("products/{id:int}/price")]
    public async Task<IActionResult> GetPrice(int id, [FromQuery] int quantity = 1, [FromQuery] int[]? valueIds = null, CancellationToken cancellationToken = default)
    {
        // Only products a customer can see; drafts, hidden or scheduled products do not exist here.
        if (await productService.GetPublicProductAsync(id, cancellationToken) is null) return NotFound();

        var result = await priceService.QuoteAsync(new PriceRequest(id, quantity, valueIds ?? []), cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
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

    // In stock on the list means: not tracked, or something on hand. The exact available quantity is on the detail.
    private static ProductResponse ToProductResponse(Product p, int mainPictureId) =>
        new(p.Id, p.Name, p.ShortDescription, p.Price, p.OldPrice, p.StockQuantity, p.ShowOnHomepage, p.DisplayOrder, p.CreatedOnUtc, p.VendorId, p.VendorName, mainPictureId,
            !p.TrackInventory || p.StockQuantity > 0,
            PriceRules.CurrentPrice(p, DateTime.UtcNow), PriceRules.IsSpecialActive(p, DateTime.UtcNow));

    private static ManufacturerResponse ToManufacturerResponse(Manufacturer m) =>
        new(m.Id, m.Name, m.Description, m.PictureId, m.DisplayOrder);
}

/// <summary>
/// Query string of the public list. Repeat a key for several values, for example <c>manufacturerIds=1&amp;manufacturerIds=2</c>.
/// Without a <c>sort</c> a search is ordered by relevance and everything else by the featured order.
/// </summary>
public sealed record ProductListRequest(
    int Page = 1, int PageSize = 20,
    int? CategoryId = null, int? ManufacturerId = null, int[]? ManufacturerIds = null,
    decimal? MinPrice = null, decimal? MaxPrice = null,
    string? Search = null,
    ProductSortOrder? Sort = null,
    int? VendorId = null,
    bool InStock = false,
    string[]? Tags = null,
    int[]? SpecOptionIds = null)
{
    public CatalogSearchRequest ToSearch() => new(
        Search, CategoryId,
        ManufacturerId is { } single ? [.. (ManufacturerIds ?? []), single] : ManufacturerIds,
        MinPrice, MaxPrice, InStock, Tags, SpecOptionIds, Sort, Page, PageSize, VendorId);
}

public sealed record ProductSuggestionResponse(int Id, string Name, decimal Price, int MainPictureId);

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
    int VendorId, string? VendorName, int MainPictureId, bool InStock, decimal FinalPrice, bool OnSale);

public sealed record ProductDetailResponse(
    ProductResponse Product, string? FullDescription,
    IReadOnlyList<CategoryResponse> Categories,
    IReadOnlyList<ManufacturerResponse> Manufacturers,
    IReadOnlyList<int> PictureIds,
    IReadOnlyList<ProductResponse> RelatedProducts,
    bool TrackInventory,
    /// <summary>What a customer can still buy: on hand minus active reservations. Meaningless when <see cref="TrackInventory"/> is false.</summary>
    int AvailableQuantity,
    /// <summary>Quantity prices, lowest quantity first: from <c>Quantity</c> units each unit costs <c>Price</c>.</summary>
    IReadOnlyList<TierPriceResponse> TierPrices);

public sealed record ManufacturerResponse(
    int Id, string Name, string? Description,
    int PictureId, int DisplayOrder);
