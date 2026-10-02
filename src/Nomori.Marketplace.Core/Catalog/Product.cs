namespace Nomori.Marketplace.Core.Catalog;

/// <summary>Product lifecycle. Only <see cref="Live"/> products can appear on the storefront.</summary>
public enum ProductStatus
{
    Draft = 0,
    Live = 1,
    Stopped = 2,
    HiddenByAdmin = 3
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public decimal Price { get; set; }
    public decimal OldPrice { get; set; }
    public int StockQuantity { get; set; }
    public ProductStatus Status { get; set; }

    /// <summary>True when the product is on sale. Derived from <see cref="Status"/>, so the two can never disagree.</summary>
    public bool Published => Status == ProductStatus.Live;

    /// <summary>Set while hidden: the state to restore when an administrator unhides the product.</summary>
    public ProductStatus? StatusBeforeHidden { get; set; }

    public string? HiddenReason { get; set; }
    public DateTime? HiddenOnUtc { get; set; }
    public int? HiddenByCustomerId { get; set; }

    /// <summary>Set when the shop asked an administrator to look at a hidden product again.</summary>
    public DateTime? ReviewRequestedOnUtc { get; set; }

    public bool Deleted { get; set; }
    /// <summary>Owning shop. Never null: platform products belong to the platform shop.</summary>
    public int VendorId { get; set; }

    /// <summary>Shop name, filled by reads only.</summary>
    public string? VendorName { get; set; }

    /// <summary>Whether the owning shop is active, filled by reads only.</summary>
    public bool VendorActive { get; set; }

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

    /// <summary>Media asset ids in display order; the first is the main picture.</summary>
    public IReadOnlyList<int> PictureIds { get; set; } = [];
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
    Task SetVendorAsync(int productId, int vendorId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Writes the lifecycle fields only: status, hidden fields and the review request.</summary>
    Task UpdateLifecycleAsync(Product product, CancellationToken cancellationToken);
    Task SetCategoriesAsync(int productId, int[] categoryIds, CancellationToken cancellationToken);

    /// <summary>Picture ids of a product in display order.</summary>
    Task<IReadOnlyList<int>> GetPictureIdsAsync(int productId, CancellationToken cancellationToken);

    /// <summary>Main picture id per product; products without pictures are absent.</summary>
    Task<IReadOnlyDictionary<int, int>> GetMainPictureIdsAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken);

    /// <summary>The product a picture is attached to, or null when it is free.</summary>
    Task<int?> GetPictureOwnerAsync(int mediaAssetId, CancellationToken cancellationToken);

    /// <summary>Replaces all pictures of a product in one transaction; the position in the array is the display order.</summary>
    Task SetPicturesAsync(int productId, int[] mediaAssetIds, CancellationToken cancellationToken);
    Task SetManufacturersAsync(int productId, int[] manufacturerIds, CancellationToken cancellationToken);
}

public interface IProductService
{
    Task<ProductDetail?> GetDetailAsync(int id, CancellationToken cancellationToken);
    Task<PagedResult<Product>> GetListAsync(ProductQuery query, CancellationToken cancellationToken);
    Task<CatalogResult<Product>> CreateAsync(CreateProductCommand command, int actorCustomerId, CancellationToken cancellationToken);

    // ---- Seller scope: every method is tied to one shop and treats products of other shops as not found ----

    Task<ProductDetail?> GetDetailForVendorAsync(int vendorId, int productId, CancellationToken cancellationToken);
    Task<PagedResult<Product>> GetListForVendorAsync(int vendorId, ProductQuery query, CancellationToken cancellationToken);
    Task<CatalogResult<Product>> CreateForVendorAsync(int vendorId, SaveVendorProductCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<Product>> UpdateForVendorAsync(int vendorId, int productId, SaveVendorProductCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteForVendorAsync(int vendorId, int productId, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Publishes (live) or stops (stopped) a product. A hidden product cannot be changed by its shop.</summary>
    Task<CatalogResult<Product>> SetStatusForVendorAsync(int vendorId, int productId, ProductStatus target, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Asks an administrator to look at a hidden product again.</summary>
    Task<CatalogResult<Product>> RequestReviewForVendorAsync(int vendorId, int productId, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Replaces the ordered pictures of a product of this shop (maximum 10; the first is the main picture).</summary>
    Task<CatalogResult<int[]>> SetPicturesForVendorAsync(int vendorId, int productId, int[]? pictureIds, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Main picture id per product, for list screens. Products without pictures are absent.</summary>
    Task<IReadOnlyDictionary<int, int>> GetMainPictureIdsAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken);

    // ---- Moderation (platform administrators) ----

    Task<CatalogResult<Product>> HideAsync(int productId, string? reason, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Restores the state the product had before it was hidden.</summary>
    Task<CatalogResult<Product>> UnhideAsync(int productId, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Platform administrators only: moves a product to another shop.</summary>
    Task<CatalogResult<Product>> TransferAsync(int productId, int newVendorId, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<Product>> UpdateAsync(UpdateProductCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteAsync(int id, int actorCustomerId, CancellationToken cancellationToken);
}
