namespace Nomori.Marketplace.Core.Catalog;

public static class SearchLimits
{
    public const int MinSearchLength = 2;
    public const int MaxSearchLength = 100;
    public const int MaxTerms = 5;
    public const int MaxManufacturers = 20;
    public const int MaxTags = 10;
    public const int MaxSpecOptions = 20;
    public const int MaxTagLength = 100;
    public const int MaxPageSize = 100;
    public const int SuggestCount = 8;
    public const int FacetTags = 20;
}

/// <summary>What a customer asked for on the storefront list. The service validates it and turns it into a public <see cref="ProductQuery"/>.</summary>
public sealed record CatalogSearchRequest(
    string? Search = null,
    int? CategoryId = null,
    int[]? ManufacturerIds = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    bool InStock = false,
    string[]? Tags = null,
    int[]? SpecOptionIds = null,
    ProductSortOrder? Sort = null,
    int Page = 1,
    int PageSize = 20,
    int? VendorId = null);

public sealed record ManufacturerFacet(int Id, string Name, int Count);

public sealed record TagFacet(string Name, int Count);

public sealed record SpecOptionFacet(int Id, string Name, int Count);

public sealed record SpecAttributeFacet(int Id, string Name, IReadOnlyList<SpecOptionFacet> Options);

/// <summary>Counts for the filter panel, taken from the result without the facet selections themselves.</summary>
public sealed record ProductFacets(
    int TotalCount,
    decimal? MinPrice,
    decimal? MaxPrice,
    IReadOnlyList<ManufacturerFacet> Manufacturers,
    IReadOnlyList<TagFacet> Tags,
    IReadOnlyList<SpecAttributeFacet> Specifications);

public interface IProductFacetStore
{
    /// <summary>Facets of the products a public query matches. Selections of manufacturers, tags and specifications in the query are ignored by the caller.</summary>
    Task<ProductFacets> GetFacetsAsync(ProductQuery query, int tagLimit, CancellationToken cancellationToken);
}

public interface ICatalogSearchService
{
    Task<CatalogResult<PagedResult<Product>>> SearchAsync(CatalogSearchRequest request, CancellationToken cancellationToken);

    /// <summary>Facets for the same filters; paging and sort of the request are ignored.</summary>
    Task<CatalogResult<ProductFacets>> GetFacetsAsync(CatalogSearchRequest request, CancellationToken cancellationToken);

    /// <summary>Up to <see cref="SearchLimits.SuggestCount"/> visible products for text typed so far.</summary>
    Task<CatalogResult<IReadOnlyList<Product>>> SuggestAsync(string? text, CancellationToken cancellationToken);
}

public static class SearchText
{
    /// <summary>Trims and splits on white space; at most <see cref="SearchLimits.MaxTerms"/> terms are kept.</summary>
    public static string[] Terms(string? search) =>
        (search ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(SearchLimits.MaxTerms).ToArray();

    /// <summary>Makes a term safe for a SQL <c>LIKE</c> pattern: the wildcard characters match themselves.</summary>
    public static string EscapeLike(string term) =>
        term.Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal);
}
