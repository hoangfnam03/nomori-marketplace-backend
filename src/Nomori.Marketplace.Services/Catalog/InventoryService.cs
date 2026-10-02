using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class InventoryService(
    IInventoryStore inventoryStore,
    IProductStore productStore,
    IVendorStore vendorStore,
    IProductAttributeStore attributeStore,
    IAuditLogService auditLog,
    IClock clock) : IInventoryService
{
    // ---- Seller ----

    public async Task<CatalogResult<InventoryOverview>> GetOverviewForVendorAsync(int vendorId, int productId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadAsync(vendorId, productId, forWrite: false, cancellationToken);
        return failure ?? CatalogResult.Success(await BuildOverviewAsync(product!, cancellationToken));
    }

    public async Task<CatalogResult<InventoryOverview>> AdjustForVendorAsync(
        int vendorId, int productId, AdjustStockCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadAsync(vendorId, productId, forWrite: true, cancellationToken);
        if (failure is not null) return failure;

        var errors = new Dictionary<string, string[]>();
        if (command.Delta == 0 || Math.Abs((long)command.Delta) > InventoryLimits.MaxDelta)
            errors["delta"] = [$"The change must be between 1 and {InventoryLimits.MaxDelta} units, up or down."];
        var reason = command.Reason?.Trim().ToLowerInvariant();
        if (reason is null || !StockReasons.SellerChoices.Contains(reason))
            errors["reason"] = [$"The reason must be one of: {string.Join(", ", StockReasons.SellerChoices)}."];
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        if (note is { Length: > InventoryLimits.MaxNoteLength })
            errors["note"] = [$"The note cannot exceed {InventoryLimits.MaxNoteLength} characters."];

        // A product with variants keeps its stock per combination; a plain product has none.
        var combinations = await attributeStore.GetCombinationsAsync(productId, cancellationToken);
        if (combinations.Count > 0)
        {
            if (command.CombinationId is not { } id || combinations.All(c => c.Id != id))
                errors["combinationId"] = ["Choose the combination whose stock changes."];
        }
        else if (command.CombinationId is not null)
        {
            errors["combinationId"] = ["This product has no combinations."];
        }
        if (errors.Count > 0) return CatalogResult.Failure<InventoryOverview>(errors);

        var change = await inventoryStore.AdjustAsync(
            new StockAdjustment(productId, command.CombinationId, command.Delta, reason!, null, note, actorCustomerId), clock.UtcNow, cancellationToken);
        if (!change.Succeeded) return CatalogResult.Error<InventoryOverview>(ToErrorCode(change.Outcome));

        await auditLog.WriteAsync("product.stock_adjusted", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId, combinationId = command.CombinationId, delta = command.Delta, reason, quantityAfter = change.OnHand },
            cancellationToken: cancellationToken);
        return CatalogResult.Success(await BuildOverviewAsync((await productStore.GetAsync(productId, cancellationToken))!, cancellationToken));
    }

    public async Task<CatalogResult<InventoryOverview>> SetSettingsForVendorAsync(
        int vendorId, int productId, bool trackInventory, int lowStockThreshold, int actorCustomerId, CancellationToken cancellationToken)
    {
        var (_, failure) = await LoadAsync(vendorId, productId, forWrite: true, cancellationToken);
        if (failure is not null) return failure;

        if (lowStockThreshold is < 0 or > InventoryLimits.MaxThreshold)
            return CatalogResult.Failure<InventoryOverview>("lowStockThreshold", $"The threshold must be between 0 and {InventoryLimits.MaxThreshold}.");

        await inventoryStore.SetSettingsAsync(productId, trackInventory, lowStockThreshold, cancellationToken);
        await auditLog.WriteAsync("product.inventory_settings_changed", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId, trackInventory, lowStockThreshold }, cancellationToken: cancellationToken);
        return CatalogResult.Success(await BuildOverviewAsync((await productStore.GetAsync(productId, cancellationToken))!, cancellationToken));
    }

    public async Task<CatalogResult<PagedResult<StockMovement>>> GetMovementsForVendorAsync(
        int vendorId, int productId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null || product.VendorId != vendorId) return CatalogResult.Error<PagedResult<StockMovement>>(CatalogErrors.NotFound);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await inventoryStore.GetMovementsAsync(productId, page, pageSize, cancellationToken);
        return CatalogResult.Success(new PagedResult<StockMovement>(items, total, page, pageSize));
    }

    // ---- Internal: cart and checkout ----

    public async Task<CatalogResult<bool>> ReserveAsync(
        string reference, int productId, int? combinationId, int quantity, int? minutes, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var trimmed = reference?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > InventoryLimits.MaxReferenceLength)
            errors["reference"] = [$"The reference must be 1 to {InventoryLimits.MaxReferenceLength} characters."];
        if (quantity is < 1 or > InventoryLimits.MaxReservationQuantity)
            errors["quantity"] = [$"The quantity must be between 1 and {InventoryLimits.MaxReservationQuantity}."];
        var ttl = minutes ?? InventoryLimits.DefaultReservationMinutes;
        if (ttl is < 1 or > InventoryLimits.MaxReservationMinutes)
            errors["minutes"] = [$"The hold must last 1 to {InventoryLimits.MaxReservationMinutes} minutes."];
        if (errors.Count > 0) return CatalogResult.Failure<bool>(errors);

        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);
        // Nothing to protect when the product has no stock limit.
        if (!product.TrackInventory) return CatalogResult.Success(true);

        var hasCombinations = (await attributeStore.GetCombinationsAsync(productId, cancellationToken)).Count > 0;
        if (hasCombinations != combinationId.HasValue)
            return CatalogResult.Failure<bool>("combinationId", hasCombinations ? "Choose a combination." : "This product has no combinations.");

        var now = clock.UtcNow;
        var change = await inventoryStore.ReserveAsync(
            new StockReservationRequest(trimmed!, productId, combinationId, quantity, now.AddMinutes(ttl)), now, cancellationToken);
        return change.Succeeded ? CatalogResult.Success(true) : CatalogResult.Error<bool>(ToErrorCode(change.Outcome));
    }

    public Task<int> ReleaseAsync(string reference, CancellationToken cancellationToken) =>
        inventoryStore.ReleaseAsync(reference?.Trim() ?? string.Empty, clock.UtcNow, cancellationToken);

    public async Task<CatalogResult<bool>> CommitAsync(string reference, CancellationToken cancellationToken)
    {
        var change = await inventoryStore.CommitAsync(reference?.Trim() ?? string.Empty, clock.UtcNow, cancellationToken);
        return change.Succeeded ? CatalogResult.Success(true) : CatalogResult.Error<bool>(ToErrorCode(change.Outcome));
    }

    // ---- Public ----

    public async Task<Availability?> GetAvailabilityAsync(int productId, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null) return null;

        var now = clock.UtcNow;
        var level = await inventoryStore.GetLevelAsync(productId, null, now, cancellationToken) ?? new StockLevel(product.StockQuantity, 0);
        var combinations = await inventoryStore.GetCombinationLevelsAsync(productId, now, cancellationToken);
        return new Availability(product.TrackInventory, level.Available, combinations.ToDictionary(kv => kv.Key, kv => kv.Value.Available));
    }

    // ---- Helpers ----

    private async Task<InventoryOverview> BuildOverviewAsync(Product product, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var level = await inventoryStore.GetLevelAsync(product.Id, null, now, cancellationToken) ?? new StockLevel(product.StockQuantity, 0);
        var levels = await inventoryStore.GetCombinationLevelsAsync(product.Id, now, cancellationToken);
        var combinations = (await attributeStore.GetCombinationsAsync(product.Id, cancellationToken))
            .Select(c => new CombinationStock(c.Id, c.Sku, c.AttributesJson, levels.GetValueOrDefault(c.Id) ?? new StockLevel(c.StockQuantity, 0)))
            .ToList();
        return new InventoryOverview(product.TrackInventory, product.LowStockThreshold, level, combinations);
    }

    private async Task<(Product? Product, CatalogResult<InventoryOverview>? Failure)> LoadAsync(
        int vendorId, int productId, bool forWrite, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null || product.VendorId != vendorId) return (null, CatalogResult.Error<InventoryOverview>(CatalogErrors.NotFound));
        if (!forWrite) return (product, null);

        var vendor = await vendorStore.GetAsync(vendorId, cancellationToken);
        if (vendor is null) return (null, CatalogResult.Error<InventoryOverview>(CatalogErrors.NotFound));
        if (!vendor.Active) return (null, CatalogResult.Error<InventoryOverview>(CatalogErrors.Forbidden));
        return (product, null);
    }

    private static string ToErrorCode(InventoryOutcome outcome) => outcome switch
    {
        InventoryOutcome.NotFound => CatalogErrors.NotFound,
        InventoryOutcome.Expired => CatalogErrors.ReservationExpired,
        _ => CatalogErrors.InsufficientStock
    };
}
