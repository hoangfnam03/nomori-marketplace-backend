namespace Nomori.Marketplace.Core.Catalog;

public sealed record CategoryQuery(int Page = 1, int PageSize = 20, int? ParentId = null, bool? Published = null);

public sealed record ProductQuery(
    int Page = 1,
    int PageSize = 20,
    int? CategoryId = null,
    int? ManufacturerId = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    string? Search = null,
    ProductSortOrder Sort = ProductSortOrder.DisplayOrder,
    bool? Published = null,
    int? VendorId = null,
    ProductStatus? Status = null,
    /// <summary>When true, only products with a pending review request.</summary>
    bool? ReviewRequested = null,
    /// <summary>When true, only tracked products at or below their low-stock threshold.</summary>
    bool? LowStock = null,
    /// <summary>Hide products whose shop is inactive or deleted. Set for every public query.</summary>
    bool OnlyActiveShops = false);

public sealed record ManufacturerQuery(int Page = 1, int PageSize = 20, bool? Published = null);

public enum ProductSortOrder
{
    DisplayOrder = 0,
    NameAsc = 1,
    NameDesc = 2,
    PriceAsc = 3,
    PriceDesc = 4,
    Newest = 5
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed record CreateCategoryCommand(
    string Name,
    string? Description,
    int ParentCategoryId,
    int PictureId,
    bool ShowOnHomepage,
    bool Published,
    int DisplayOrder,
    bool RestrictFromVendors = false);

public sealed record UpdateCategoryCommand(
    int Id,
    string Name,
    string? Description,
    int ParentCategoryId,
    int PictureId,
    bool ShowOnHomepage,
    bool Published,
    int DisplayOrder,
    bool RestrictFromVendors = false);

public sealed record CreateProductCommand(
    string Name,
    string? ShortDescription,
    string? FullDescription,
    decimal Price,
    decimal OldPrice,
    int StockQuantity,
    bool Published,
    int? VendorId,
    bool ShowOnHomepage,
    int DisplayOrder,
    int[] CategoryIds,
    int[] ManufacturerIds);

/// <summary>Seller input. Has no owner, homepage, ordering or status fields: sellers change status through their own actions.</summary>
public sealed record SaveVendorProductCommand(
    string Name,
    string? ShortDescription,
    string? FullDescription,
    decimal Price,
    decimal OldPrice,
    int StockQuantity,
    int[] CategoryIds,
    int[] ManufacturerIds,
    string? Sku = null,
    string? Gtin = null,
    string? ManufacturerPartNumber = null,
    DateTime? AvailableStartUtc = null,
    DateTime? AvailableEndUtc = null);

public sealed record UpdateProductCommand(
    int Id,
    string Name,
    string? ShortDescription,
    string? FullDescription,
    decimal Price,
    decimal OldPrice,
    int StockQuantity,
    bool Published,
    int? VendorId,
    bool ShowOnHomepage,
    int DisplayOrder,
    int[] CategoryIds,
    int[] ManufacturerIds);

public sealed record CreateManufacturerCommand(
    string Name,
    string? Description,
    int PictureId,
    bool Published,
    int DisplayOrder);

public sealed record UpdateManufacturerCommand(
    int Id,
    string Name,
    string? Description,
    int PictureId,
    bool Published,
    int DisplayOrder);

/// <summary>
/// Outcome of a catalog operation: field errors (400), an <see cref="ErrorCode"/> from <see cref="CatalogErrors"/>
/// (not_found is 404, every other code is a 409 business-rule conflict), or a value.
/// </summary>
public sealed record CatalogResult<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors, string? ErrorCode = null)
{
    public bool Succeeded => Errors.Count == 0 && ErrorCode is null;
}

public static class CatalogResult
{
    public static CatalogResult<T> Success<T>(T value) =>
        new(value, new Dictionary<string, string[]>());

    public static CatalogResult<T> Failure<T>(string field, string message) =>
        new(default, new Dictionary<string, string[]> { [field] = [message] });

    public static CatalogResult<T> Failure<T>(IReadOnlyDictionary<string, string[]> errors) =>
        new(default, errors);

    public static CatalogResult<T> Error<T>(string errorCode) =>
        new(default, new Dictionary<string, string[]>(), errorCode);
}
