using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class ProductService(
    IProductStore productStore,
    ICategoryStore categoryStore,
    IManufacturerStore manufacturerStore,
    IVendorStore vendorStore,
    ITaxonomyService taxonomy,
    IAuditLogService auditLog,
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

    // ---- Administrators ----

    public async Task<CatalogResult<Product>> CreateAsync(CreateProductCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = Validate(command.Name, command.Price, command.OldPrice, command.StockQuantity);
        var selection = await taxonomy.ValidateSelectionAsync(command.CategoryIds, command.ManufacturerIds, TaxonomyAudience.Admin, cancellationToken);
        foreach (var error in selection.Errors) errors[error.Key] = error.Value;

        // No owner given means a platform product.
        var vendorId = command.VendorId;
        if (vendorId is null)
        {
            var platform = await vendorStore.GetPlatformShopAsync(cancellationToken);
            if (platform is null) errors["vendorId"] = ["The platform shop is missing. Run the database migrations."];
            else vendorId = platform.Id;
        }
        else if (await vendorStore.GetAsync(vendorId.Value, cancellationToken) is null)
        {
            errors["vendorId"] = ["Shop does not exist."];
        }

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
            VendorId = vendorId!.Value,
            ShowOnHomepage = command.ShowOnHomepage,
            DisplayOrder = command.DisplayOrder,
            CreatedOnUtc = now,
            UpdatedOnUtc = now
        };
        await InsertWithMappingsAsync(product, selection.Value!, cancellationToken);
        await AuditAsync("product.created", actorCustomerId, product, cancellationToken);
        return CatalogResult.Success(product);
    }

    public async Task<CatalogResult<Product>> UpdateAsync(UpdateProductCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var existing = await productStore.GetAsync(command.Id, cancellationToken);
        if (existing is null) return CatalogResult.Error<Product>(CatalogErrors.NotFound);

        var errors = Validate(command.Name, command.Price, command.OldPrice, command.StockQuantity);
        var selection = await taxonomy.ValidateSelectionAsync(command.CategoryIds, command.ManufacturerIds, TaxonomyAudience.Admin, cancellationToken);
        foreach (var error in selection.Errors) errors[error.Key] = error.Value;

        // The owner is fixed after creation. Omitting vendorId (or repeating the current one) is fine.
        if (command.VendorId is { } requested && requested != existing.VendorId)
            errors["vendorId"] = ["The owner of a product cannot be changed here. Use the transfer action."];
        if (errors.Count > 0) return CatalogResult.Failure<Product>(errors);

        existing.Name = command.Name.Trim();
        existing.ShortDescription = NullIfBlank(command.ShortDescription);
        existing.FullDescription = NullIfBlank(command.FullDescription);
        existing.Price = command.Price;
        existing.OldPrice = command.OldPrice;
        existing.StockQuantity = command.StockQuantity;
        existing.Published = command.Published;
        existing.ShowOnHomepage = command.ShowOnHomepage;
        existing.DisplayOrder = command.DisplayOrder;
        existing.UpdatedOnUtc = clock.UtcNow;
        await productStore.UpdateAsync(existing, cancellationToken);
        await productStore.SetCategoriesAsync(existing.Id, selection.Value!.CategoryIds, cancellationToken);
        await productStore.SetManufacturersAsync(existing.Id, selection.Value.ManufacturerIds, cancellationToken);

        await AuditAsync("product.updated", actorCustomerId, existing, cancellationToken);
        return CatalogResult.Success(existing);
    }

    public async Task<CatalogResult<bool>> DeleteAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var existing = await productStore.GetAsync(id, cancellationToken);
        if (existing is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);

        await productStore.DeleteAsync(id, cancellationToken);
        await AuditAsync("product.deleted", actorCustomerId, existing, cancellationToken);
        return CatalogResult.Success(true);
    }

    public async Task<CatalogResult<Product>> TransferAsync(int productId, int newVendorId, int actorCustomerId, CancellationToken cancellationToken)
    {
        var existing = await productStore.GetAsync(productId, cancellationToken);
        if (existing is null) return CatalogResult.Error<Product>(CatalogErrors.NotFound);

        if (existing.VendorId == newVendorId)
            return CatalogResult.Failure<Product>("vendorId", "The product already belongs to this shop.");
        if (await vendorStore.GetAsync(newVendorId, cancellationToken) is null)
            return CatalogResult.Failure<Product>("vendorId", "Shop does not exist.");

        var previousVendorId = existing.VendorId;
        var now = clock.UtcNow;
        await productStore.SetVendorAsync(productId, newVendorId, now, cancellationToken);
        existing.VendorId = newVendorId;
        existing.UpdatedOnUtc = now;

        await auditLog.WriteAsync("product.transferred", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, fromVendorId = previousVendorId, toVendorId = newVendorId },
            cancellationToken: cancellationToken);
        return CatalogResult.Success(existing);
    }

    // ---- Sellers: one shop at a time; products of other shops do not exist for the caller ----

    public async Task<ProductDetail?> GetDetailForVendorAsync(int vendorId, int productId, CancellationToken cancellationToken)
    {
        var detail = await GetDetailAsync(productId, cancellationToken);
        return detail is not null && detail.Product.VendorId == vendorId ? detail : null;
    }

    public Task<PagedResult<Product>> GetListForVendorAsync(int vendorId, ProductQuery query, CancellationToken cancellationToken) =>
        GetListAsync(query with { VendorId = vendorId }, cancellationToken);

    public async Task<CatalogResult<Product>> CreateForVendorAsync(
        int vendorId, SaveVendorProductCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var vendor = await vendorStore.GetAsync(vendorId, cancellationToken);
        if (vendor is null) return CatalogResult.Error<Product>(CatalogErrors.NotFound);
        if (!vendor.Active) return CatalogResult.Error<Product>(CatalogErrors.Forbidden);

        var (errors, selection) = await ValidateSellerAsync(command, cancellationToken);
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
            VendorId = vendorId,
            // Admin-only fields: sellers start from the defaults.
            ShowOnHomepage = false,
            DisplayOrder = 0,
            CreatedOnUtc = now,
            UpdatedOnUtc = now
        };
        await InsertWithMappingsAsync(product, selection!, cancellationToken);
        await AuditAsync("product.created", actorCustomerId, product, cancellationToken);
        return CatalogResult.Success(product);
    }

    public async Task<CatalogResult<Product>> UpdateForVendorAsync(
        int vendorId, int productId, SaveVendorProductCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var existing = await productStore.GetAsync(productId, cancellationToken);
        if (existing is null || existing.VendorId != vendorId) return CatalogResult.Error<Product>(CatalogErrors.NotFound);

        var vendor = await vendorStore.GetAsync(vendorId, cancellationToken);
        if (vendor is null) return CatalogResult.Error<Product>(CatalogErrors.NotFound);
        if (!vendor.Active) return CatalogResult.Error<Product>(CatalogErrors.Forbidden);

        var (errors, selection) = await ValidateSellerAsync(command, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<Product>(errors);

        existing.Name = command.Name.Trim();
        existing.ShortDescription = NullIfBlank(command.ShortDescription);
        existing.FullDescription = NullIfBlank(command.FullDescription);
        existing.Price = command.Price;
        existing.OldPrice = command.OldPrice;
        existing.StockQuantity = command.StockQuantity;
        existing.Published = command.Published;
        // ShowOnHomepage and DisplayOrder keep their current (admin-controlled) values.
        existing.UpdatedOnUtc = clock.UtcNow;
        await productStore.UpdateAsync(existing, cancellationToken);
        await productStore.SetCategoriesAsync(existing.Id, selection!.CategoryIds, cancellationToken);
        await productStore.SetManufacturersAsync(existing.Id, selection.ManufacturerIds, cancellationToken);

        await AuditAsync("product.updated", actorCustomerId, existing, cancellationToken);
        return CatalogResult.Success(existing);
    }

    public async Task<CatalogResult<bool>> DeleteForVendorAsync(int vendorId, int productId, int actorCustomerId, CancellationToken cancellationToken)
    {
        var existing = await productStore.GetAsync(productId, cancellationToken);
        if (existing is null || existing.VendorId != vendorId) return CatalogResult.Error<bool>(CatalogErrors.NotFound);

        var vendor = await vendorStore.GetAsync(vendorId, cancellationToken);
        if (vendor is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);
        if (!vendor.Active) return CatalogResult.Error<bool>(CatalogErrors.Forbidden);

        await productStore.DeleteAsync(productId, cancellationToken);
        await AuditAsync("product.deleted", actorCustomerId, existing, cancellationToken);
        return CatalogResult.Success(true);
    }

    // ---- Helpers ----

    private async Task InsertWithMappingsAsync(Product product, TaxonomySelection selection, CancellationToken cancellationToken)
    {
        product.Id = await productStore.InsertAsync(product, cancellationToken);
        if (selection.CategoryIds.Length > 0)
            await productStore.SetCategoriesAsync(product.Id, selection.CategoryIds, cancellationToken);
        if (selection.ManufacturerIds.Length > 0)
            await productStore.SetManufacturersAsync(product.Id, selection.ManufacturerIds, cancellationToken);
    }

    private async Task<(Dictionary<string, string[]> Errors, TaxonomySelection? Selection)> ValidateSellerAsync(
        SaveVendorProductCommand command, CancellationToken cancellationToken)
    {
        var errors = Validate(command.Name, command.Price, command.OldPrice, command.StockQuantity);
        if (command.Price <= 0) errors["price"] = ["Price must be greater than 0."];
        if (command.OldPrice != 0 && command.OldPrice <= command.Price)
            errors["oldPrice"] = ["Old price must be greater than the price, or 0 for none."];

        var selection = await taxonomy.ValidateSelectionAsync(command.CategoryIds, command.ManufacturerIds, TaxonomyAudience.Seller, cancellationToken);
        foreach (var error in selection.Errors) errors[error.Key] = error.Value;

        if (command.Published && !errors.ContainsKey("price") && (selection.Value?.CategoryIds.Length ?? command.CategoryIds.Length) == 0)
            errors["published"] = ["A product needs at least one category before it can be published."];

        return (errors, selection.Value);
    }

    // Audit entries carry ids only, never product text.
    private Task AuditAsync(string eventName, int actorCustomerId, Product product, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(eventName, actorCustomerId, entityType: "Product", entityId: product.Id,
            details: new { productId = product.Id, vendorId = product.VendorId }, cancellationToken: cancellationToken);

    private static Dictionary<string, string[]> Validate(string? name, decimal price, decimal oldPrice, int stockQuantity)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Product name is required."];
        else if (name.Trim().Length > 400) errors["name"] = ["Product name cannot exceed 400 characters."];
        if (price < 0) errors["price"] = ["Price cannot be negative."];
        if (oldPrice < 0) errors["oldPrice"] = ["Old price cannot be negative."];
        if (stockQuantity < 0) errors["stockQuantity"] = ["Stock quantity cannot be negative."];
        return errors;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
