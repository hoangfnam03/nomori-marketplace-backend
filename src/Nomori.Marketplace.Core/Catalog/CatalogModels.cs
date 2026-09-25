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
    bool? Published = null);

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
    int DisplayOrder);

public sealed record UpdateCategoryCommand(
    int Id,
    string Name,
    string? Description,
    int ParentCategoryId,
    int PictureId,
    bool ShowOnHomepage,
    bool Published,
    int DisplayOrder);

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

public sealed record CatalogResult<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

public static class CatalogResult
{
    public static CatalogResult<T> Success<T>(T value) =>
        new(value, new Dictionary<string, string[]>());

    public static CatalogResult<T> Failure<T>(string field, string message) =>
        new(default, new Dictionary<string, string[]> { [field] = [message] });

    public static CatalogResult<T> Failure<T>(IReadOnlyDictionary<string, string[]> errors) =>
        new(default, errors);
}
