namespace Nomori.Marketplace.Core.Catalog;

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public decimal Price { get; set; }
    public decimal OldPrice { get; set; }
    public int StockQuantity { get; set; }
    public bool Published { get; set; }
    public bool Deleted { get; set; }
    public int? VendorId { get; set; }
    public bool ShowOnHomepage { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public sealed class ProductDetail
{
    public Product Product { get; set; } = null!;
    public IReadOnlyList<Category> Categories { get; set; } = [];
    public IReadOnlyList<Manufacturer> Manufacturers { get; set; } = [];
}

public interface IProductStore
{
    Task<Product?> GetAsync(int id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Product> Items, int TotalCount)> GetPagedAsync(ProductQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<int>> GetCategoryIdsAsync(int productId, CancellationToken cancellationToken);
    Task<IReadOnlyList<int>> GetManufacturerIdsAsync(int productId, CancellationToken cancellationToken);
    Task<int> InsertAsync(Product product, CancellationToken cancellationToken);
    Task UpdateAsync(Product product, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
    Task SetCategoriesAsync(int productId, int[] categoryIds, CancellationToken cancellationToken);
    Task SetManufacturersAsync(int productId, int[] manufacturerIds, CancellationToken cancellationToken);
}

public interface IProductService
{
    Task<ProductDetail?> GetDetailAsync(int id, CancellationToken cancellationToken);
    Task<PagedResult<Product>> GetListAsync(ProductQuery query, CancellationToken cancellationToken);
    Task<CatalogResult<Product>> CreateAsync(CreateProductCommand command, CancellationToken cancellationToken);
    Task<CatalogResult<Product>> UpdateAsync(UpdateProductCommand command, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
}
