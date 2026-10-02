using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class CatalogSearchService(
    IProductStore productStore,
    IProductFacetStore facetStore,
    ICategoryService categoryService,
    ISpecificationAttributeStore specStore) : ICatalogSearchService
{
    public async Task<CatalogResult<PagedResult<Product>>> SearchAsync(CatalogSearchRequest request, CancellationToken cancellationToken)
    {
        var (errors, query) = await BuildAsync(request, forFacets: false, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<PagedResult<Product>>(errors);

        // A category that is not public (or does not exist) has no products to show.
        if (query is null) return CatalogResult.Success(new PagedResult<Product>([], 0, Math.Max(request.Page, 1), PageSize(request)));

        var (items, total) = await productStore.GetPagedAsync(query, cancellationToken);
        return CatalogResult.Success(new PagedResult<Product>(items, total, query.Page, query.PageSize));
    }

    public async Task<CatalogResult<ProductFacets>> GetFacetsAsync(CatalogSearchRequest request, CancellationToken cancellationToken)
    {
        var (errors, query) = await BuildAsync(request, forFacets: true, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<ProductFacets>(errors);
        if (query is null) return CatalogResult.Success(new ProductFacets(0, null, null, [], [], []));

        return CatalogResult.Success(await facetStore.GetFacetsAsync(query, SearchLimits.FacetTags, cancellationToken));
    }

    public async Task<CatalogResult<IReadOnlyList<Product>>> SuggestAsync(string? text, CancellationToken cancellationToken)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        // Too short to be useful (and too expensive for a LIKE scan): no suggestions, not an error.
        if (trimmed.Length < SearchLimits.MinSearchLength || trimmed.Length > SearchLimits.MaxSearchLength)
            return CatalogResult.Success<IReadOnlyList<Product>>([]);

        var query = new ProductQuery(1, SearchLimits.SuggestCount, Search: trimmed, Sort: ProductSortOrder.Relevance, Published: true, OnlyActiveShops: true);
        var (items, _) = await productStore.GetPagedAsync(query, cancellationToken);
        return CatalogResult.Success(items);
    }

    /// <summary>
    /// Validates the request and builds the public query. A null query with no errors means the category is not public, so the answer is empty.
    /// For facets the selections of manufacturers, tags and specifications are left out, so their options do not vanish once one is chosen.
    /// </summary>
    private async Task<(Dictionary<string, string[]> Errors, ProductQuery? Query)> BuildAsync(
        CatalogSearchRequest request, bool forFacets, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search) && (search.Length < SearchLimits.MinSearchLength || search.Length > SearchLimits.MaxSearchLength))
            errors["search"] = [$"Search needs {SearchLimits.MinSearchLength} to {SearchLimits.MaxSearchLength} characters."];

        if (request.MinPrice is < 0) errors["minPrice"] = ["The minimum price cannot be negative."];
        if (request.MaxPrice is < 0) errors["maxPrice"] = ["The maximum price cannot be negative."];
        if (request.MinPrice is { } min && request.MaxPrice is { } max && min > max)
            errors["maxPrice"] = ["The maximum price must not be lower than the minimum price."];

        var manufacturerIds = (request.ManufacturerIds ?? []).Where(id => id > 0).Distinct().ToArray();
        if (manufacturerIds.Length > SearchLimits.MaxManufacturers)
            errors["manufacturerIds"] = [$"Choose at most {SearchLimits.MaxManufacturers} manufacturers."];

        var tags = (request.Tags ?? []).Select(t => (t ?? string.Empty).Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToArray();
        if (tags.Length > SearchLimits.MaxTags || tags.Any(t => t.Length > SearchLimits.MaxTagLength))
            errors["tags"] = [$"Choose at most {SearchLimits.MaxTags} tags."];

        var optionIds = (request.SpecOptionIds ?? []).Where(id => id > 0).Distinct().ToArray();
        if (optionIds.Length > SearchLimits.MaxSpecOptions)
            errors["specOptionIds"] = [$"Choose at most {SearchLimits.MaxSpecOptions} specification values."];

        if (errors.Count > 0) return (errors, null);

        int[]? categoryIds = null;
        if (request.CategoryId is { } categoryId)
        {
            categoryIds = await PublicCategoryAndDescendantsAsync(categoryId, cancellationToken);
            if (categoryIds is null) return (errors, null);
        }

        IReadOnlyList<int[]>? groups = null;
        if (optionIds.Length > 0 && !forFacets)
        {
            // Options of one specification attribute are alternatives; different attributes all have to match.
            var options = await specStore.GetOptionsByIdsAsync(optionIds, cancellationToken);
            groups = options.GroupBy(o => o.SpecificationAttributeId).Select(g => g.Select(o => o.Id).ToArray()).ToList();
        }

        var hasText = SearchText.Terms(search).Length > 0;
        var sort = request.Sort ?? (hasText ? ProductSortOrder.Relevance : ProductSortOrder.DisplayOrder);
        var query = new ProductQuery(
            Math.Max(request.Page, 1), PageSize(request),
            MinPrice: request.MinPrice, MaxPrice: request.MaxPrice, Search: search, Sort: sort,
            Published: true, VendorId: request.VendorId, OnlyActiveShops: true,
            CategoryIds: categoryIds,
            ManufacturerIds: forFacets || manufacturerIds.Length == 0 ? null : manufacturerIds,
            Tags: forFacets || tags.Length == 0 ? null : tags,
            SpecOptionGroups: groups is { Count: > 0 } ? groups : null,
            InStockOnly: request.InStock);
        return (errors, query);
    }

    private static int PageSize(CatalogSearchRequest request) => Math.Clamp(request.PageSize, 1, SearchLimits.MaxPageSize);

    /// <summary>The category and its published descendants, or null when it is not in the public tree.</summary>
    private async Task<int[]?> PublicCategoryAndDescendantsAsync(int categoryId, CancellationToken cancellationToken)
    {
        var node = Find(await categoryService.GetTreeAsync(cancellationToken), categoryId);
        if (node is null) return null;

        var ids = new List<int>();
        void Collect(CategoryTreeNode n)
        {
            ids.Add(n.Id);
            foreach (var child in n.Children) Collect(child);
        }
        Collect(node);
        return [.. ids];
    }

    private static CategoryTreeNode? Find(IReadOnlyList<CategoryTreeNode> nodes, int id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            if (Find(node.Children, id) is { } found) return found;
        }
        return null;
    }
}
