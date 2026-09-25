using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class ProductService(
    IProductStore productStore,
    ICategoryStore categoryStore,
    IManufacturerStore manufacturerStore,
    IClock clock) : IProductService
{
    public async Task<ProductDetail?> GetDetailAsync(int id, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(id, cancellationToken);
        if (product is null) return null;

        var categoryIds = await productStore.GetCategoryIdsAsync(id, cancellationToken);
        var manufacturerIds = await productStore.GetManufacturerIdsAsync(id, cancellationToken);

        var categories = new List<Category>();
        foreach (var cid in categoryIds)
        {
            var cat = await categoryStore.GetAsync(cid, cancellationToken);
            if (cat is not null) categories.Add(cat);
        }

        var manufacturers = new List<Manufacturer>();
        foreach (var mid in manufacturerIds)
        {
            var mfr = await manufacturerStore.GetAsync(mid, cancellationToken);
            if (mfr is not null) manufacturers.Add(mfr);
        }

        return new ProductDetail { Product = product, Categories = categories, Manufacturers = manufacturers };
    }

    public async Task<PagedResult<Product>> GetListAsync(ProductQuery query, CancellationToken cancellationToken)
    {
        var (items, total) = await productStore.GetPagedAsync(query, cancellationToken);
        return new PagedResult<Product>(items, total, query.Page, query.PageSize);
    }

    public async Task<CatalogResult<Product>> CreateAsync(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var errors = Validate(command.Name, command.Price, command.OldPrice, command.StockQuantity);
        if (errors.Count > 0) return CatalogResult.Failure<Product>(errors);

        var now = clock.UtcNow;
        var product = new Product
        {
            Name = command.Name.Trim(),
            ShortDescription = NullIfBlank(command.ShortDescription),
            FullDescription = NullIfBlank(command.FullDescription),
            Price = command.Price,
            OldPrice = command.OldPrice,
            StockQuantity = command.StockQuantity,
            Published = command.Published,
            VendorId = command.VendorId,
            ShowOnHomepage = command.ShowOnHomepage,
            DisplayOrder = command.DisplayOrder,
            CreatedOnUtc = now,
            UpdatedOnUtc = now
        };
        product.Id = await productStore.InsertAsync(product, cancellationToken);

        if (command.CategoryIds.Length > 0)
            await productStore.SetCategoriesAsync(product.Id, command.CategoryIds, cancellationToken);
        if (command.ManufacturerIds.Length > 0)
            await productStore.SetManufacturersAsync(product.Id, command.ManufacturerIds, cancellationToken);

        return CatalogResult.Success(product);
    }

    public async Task<CatalogResult<Product>> UpdateAsync(UpdateProductCommand command, CancellationToken cancellationToken)
    {
        var existing = await productStore.GetAsync(command.Id, cancellationToken);
        if (existing is null) return CatalogResult.Failure<Product>("id", "Product not found.");

        var errors = Validate(command.Name, command.Price, command.OldPrice, command.StockQuantity);
        if (errors.Count > 0) return CatalogResult.Failure<Product>(errors);

        existing.Name = command.Name.Trim();
        existing.ShortDescription = NullIfBlank(command.ShortDescription);
        existing.FullDescription = NullIfBlank(command.FullDescription);
        existing.Price = command.Price;
        existing.OldPrice = command.OldPrice;
        existing.StockQuantity = command.StockQuantity;
        existing.Published = command.Published;
        existing.VendorId = command.VendorId;
        existing.ShowOnHomepage = command.ShowOnHomepage;
        existing.DisplayOrder = command.DisplayOrder;
        existing.UpdatedOnUtc = clock.UtcNow;
        await productStore.UpdateAsync(existing, cancellationToken);
        await productStore.SetCategoriesAsync(command.Id, command.CategoryIds, cancellationToken);
        await productStore.SetManufacturersAsync(command.Id, command.ManufacturerIds, cancellationToken);

        return CatalogResult.Success(existing);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var existing = await productStore.GetAsync(id, cancellationToken);
        if (existing is null) return false;
        await productStore.DeleteAsync(id, cancellationToken);
        return true;
    }

    private static Dictionary<string, string[]> Validate(string name, decimal price, decimal oldPrice, int stockQuantity)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Product name is required."];
        else if (name.Length > 400) errors["name"] = ["Product name cannot exceed 400 characters."];
        if (price < 0) errors["price"] = ["Price cannot be negative."];
        if (oldPrice < 0) errors["oldPrice"] = ["Old price cannot be negative."];
        if (stockQuantity < 0) errors["stockQuantity"] = ["Stock quantity cannot be negative."];
        return errors;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
