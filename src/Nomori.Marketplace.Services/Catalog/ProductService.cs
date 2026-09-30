using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Catalog;

public sealed partial class ProductService(
    IProductStore productStore,
    ICategoryStore categoryStore,
    IManufacturerStore manufacturerStore,
    IVendorStore vendorStore,
    IVendorMemberStore memberStore,
    ITaxonomyService taxonomy,
    IAuditLogService auditLog,
    IEmailSender emailSender,
    IOptions<EmailOptions> emailOptions,
    ILogger<ProductService> logger,
    IClock clock) : IProductService
{
    private const int MaxReasonLength = 2000;

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
            Status = command.Published ? ProductStatus.Live : ProductStatus.Draft,
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
        ApplyPublishedFlag(existing, command.Published);
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
            Status = ProductStatus.Draft,
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
        // A product on sale must always keep a category.
        if (existing.Status == ProductStatus.Live && (selection?.CategoryIds.Length ?? 0) == 0 && !errors.ContainsKey("categoryIds"))
            errors["categoryIds"] = ["A product on sale needs at least one category. Stop selling it first."];
        if (errors.Count > 0) return CatalogResult.Failure<Product>(errors);

        existing.Name = command.Name.Trim();
        existing.ShortDescription = NullIfBlank(command.ShortDescription);
        existing.FullDescription = NullIfBlank(command.FullDescription);
        existing.Price = command.Price;
        existing.OldPrice = command.OldPrice;
        existing.StockQuantity = command.StockQuantity;
        // Status, ShowOnHomepage and DisplayOrder keep their current values: sellers change status through their own actions.
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

    // ---- Lifecycle: seller actions ----

    public async Task<CatalogResult<Product>> SetStatusForVendorAsync(
        int vendorId, int productId, ProductStatus target, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (target is not (ProductStatus.Live or ProductStatus.Stopped))
            return CatalogResult.Failure<Product>("status", "Status must be live or stopped.");

        var (product, failure) = await LoadOwnedForWriteAsync(vendorId, productId, cancellationToken);
        if (failure is not null) return failure;

        if (product!.Status == ProductStatus.HiddenByAdmin) return CatalogResult.Error<Product>(CatalogErrors.ProductHiddenByAdmin);
        if (product.Status == target) return CatalogResult.Success(product);

        if (target == ProductStatus.Stopped)
        {
            // Only a product on sale can be stopped; a draft has nothing to stop.
            if (product.Status != ProductStatus.Live) return CatalogResult.Error<Product>(CatalogErrors.ProductInvalidTransition);
        }
        else
        {
            var errors = new Dictionary<string, string[]>();
            if (product.Price <= 0) errors["price"] = ["Price must be greater than 0 before the product can be published."];
            if ((await productStore.GetCategoryIdsAsync(productId, cancellationToken)).Count == 0)
                errors["categoryIds"] = ["Add at least one category before the product can be published."];
            if (errors.Count > 0) return CatalogResult.Failure<Product>(errors);
        }

        var previous = product.Status;
        product.Status = target;
        product.UpdatedOnUtc = clock.UtcNow;
        await productStore.UpdateLifecycleAsync(product, cancellationToken);

        await auditLog.WriteAsync(target == ProductStatus.Live ? "product.published" : "product.stopped", actorCustomerId,
            entityType: "Product", entityId: productId,
            details: new { productId, vendorId, from = previous.ToString() }, cancellationToken: cancellationToken);
        return CatalogResult.Success(product);
    }

    public async Task<CatalogResult<Product>> RequestReviewForVendorAsync(
        int vendorId, int productId, int actorCustomerId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadOwnedForWriteAsync(vendorId, productId, cancellationToken);
        if (failure is not null) return failure;
        if (product!.Status != ProductStatus.HiddenByAdmin) return CatalogResult.Error<Product>(CatalogErrors.ProductNotHidden);

        product.ReviewRequestedOnUtc = clock.UtcNow;
        product.UpdatedOnUtc = product.ReviewRequestedOnUtc.Value;
        await productStore.UpdateLifecycleAsync(product, cancellationToken);

        await auditLog.WriteAsync("product.review_requested", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId }, cancellationToken: cancellationToken);
        return CatalogResult.Success(product);
    }

    // ---- Moderation: administrators ----

    public async Task<CatalogResult<Product>> HideAsync(int productId, string? reason, int actorCustomerId, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null) return CatalogResult.Error<Product>(CatalogErrors.NotFound);

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return CatalogResult.Failure<Product>("reason", "A reason is required when hiding a product.");
        if (trimmed.Length > MaxReasonLength) return CatalogResult.Failure<Product>("reason", $"Reason cannot exceed {MaxReasonLength} characters.");
        if (product.Status == ProductStatus.HiddenByAdmin) return CatalogResult.Error<Product>(CatalogErrors.ProductAlreadyHidden);

        var now = clock.UtcNow;
        var previous = product.Status;
        product.StatusBeforeHidden = previous;
        product.Status = ProductStatus.HiddenByAdmin;
        product.HiddenReason = trimmed;
        product.HiddenOnUtc = now;
        product.HiddenByCustomerId = actorCustomerId;
        product.ReviewRequestedOnUtc = null;
        product.UpdatedOnUtc = now;
        await productStore.UpdateLifecycleAsync(product, cancellationToken);

        // The reason is shown to the shop but deliberately kept out of the audit log.
        await auditLog.WriteAsync("product.hidden", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId = product.VendorId, previousStatus = previous.ToString(), reasonProvided = true },
            cancellationToken: cancellationToken);

        await NotifyMembersAsync(product, emailOptions.Value.ProductHiddenSubject,
            encoder => $"<p>Your product <strong>{encoder.Encode(product.Name)}</strong> was hidden from the storefront by an administrator.</p><p>Reason: {encoder.Encode(trimmed)}</p><p>You can still edit it and ask for a review from your vendor portal.</p>",
            cancellationToken);
        return CatalogResult.Success(product);
    }

    public async Task<CatalogResult<Product>> UnhideAsync(int productId, int actorCustomerId, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null) return CatalogResult.Error<Product>(CatalogErrors.NotFound);
        if (product.Status != ProductStatus.HiddenByAdmin) return CatalogResult.Error<Product>(CatalogErrors.ProductNotHidden);

        // Restore what the shop had; the fallback only covers rows that predate the column.
        var restored = product.StatusBeforeHidden is { } before && before != ProductStatus.HiddenByAdmin ? before : ProductStatus.Stopped;
        product.Status = restored;
        product.StatusBeforeHidden = null;
        product.HiddenReason = null;
        product.HiddenOnUtc = null;
        product.HiddenByCustomerId = null;
        product.ReviewRequestedOnUtc = null;
        product.UpdatedOnUtc = clock.UtcNow;
        await productStore.UpdateLifecycleAsync(product, cancellationToken);

        await auditLog.WriteAsync("product.unhidden", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId = product.VendorId, restoredStatus = restored.ToString() }, cancellationToken: cancellationToken);

        await NotifyMembersAsync(product, emailOptions.Value.ProductUnhiddenSubject,
            encoder => $"<p>Your product <strong>{encoder.Encode(product.Name)}</strong> is no longer hidden.</p>",
            cancellationToken);
        return CatalogResult.Success(product);
    }

    // ---- Helpers ----

    /// <summary>The product when it belongs to the shop and the shop is active; otherwise the matching failure.</summary>
    private async Task<(Product? Product, CatalogResult<Product>? Failure)> LoadOwnedForWriteAsync(
        int vendorId, int productId, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null || product.VendorId != vendorId) return (null, CatalogResult.Error<Product>(CatalogErrors.NotFound));

        var vendor = await vendorStore.GetAsync(vendorId, cancellationToken);
        if (vendor is null) return (null, CatalogResult.Error<Product>(CatalogErrors.NotFound));
        if (!vendor.Active) return (null, CatalogResult.Error<Product>(CatalogErrors.Forbidden));
        return (product, null);
    }

    /// <summary>Admin form checkbox: checked puts a product on sale, unchecked stops a live product. A hidden product is never changed here.</summary>
    private static void ApplyPublishedFlag(Product product, bool published)
    {
        if (product.Status == ProductStatus.HiddenByAdmin) return;
        if (published) product.Status = ProductStatus.Live;
        else if (product.Status == ProductStatus.Live) product.Status = ProductStatus.Stopped;
    }

    /// <summary>Emails every member of the shop. A delivery failure is logged and never undoes the moderation action.</summary>
    private async Task NotifyMembersAsync(Product product, string subject, Func<HtmlEncoder, string> body, CancellationToken cancellationToken)
    {
        if (!emailOptions.Value.Enabled) return;

        try
        {
            var members = await memberStore.ListAsync(product.VendorId, cancellationToken);
            var html = body(HtmlEncoder.Default);
            foreach (var member in members)
                await emailSender.SendEmailAsync(new EmailMessage(member.Email, subject, html, null), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotificationFailed(ex, product.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to email shop members about product {ProductId}.")]
    private partial void LogNotificationFailed(Exception exception, int productId);

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
