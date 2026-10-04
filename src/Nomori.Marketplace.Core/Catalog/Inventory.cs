namespace Nomori.Marketplace.Core.Catalog;

public static class StockReasons
{
    public const string Initial = "initial";
    public const string Restock = "restock";
    public const string Correction = "correction";
    public const string Damage = "damage";
    public const string Return = "return";
    public const string VariantsSaved = "variants_saved";
    public const string AdminEdit = "admin_edit";
    public const string Sale = "sale";

    /// <summary>Stock put back because an order was cancelled.</summary>
    public const string OrderCancelled = "order_cancelled";

    /// <summary>What a seller may pick for a manual adjustment.</summary>
    public static readonly IReadOnlyList<string> SellerChoices = [Restock, Correction, Damage, Return];
}

public static class InventoryLimits
{
    public const int MaxDelta = 1_000_000;
    public const int MaxReservationQuantity = 1_000;
    public const int MaxReferenceLength = 100;
    public const int MaxNoteLength = 500;
    public const int MaxThreshold = 100_000;
    public const int DefaultThreshold = 5;
    public const int DefaultReservationMinutes = 30;
    public const int MaxReservationMinutes = 1_440;
}

/// <summary>The stock arithmetic. Stores read the locked row, ask these rules, then write, so the rules stay testable.</summary>
public static class StockRules
{
    /// <summary>What a customer can still buy. Never negative, even if stock was reduced below what is reserved.</summary>
    public static int Available(int onHand, int reserved) => Math.Max(0, onHand - reserved);

    /// <summary>An adjustment may not make the stock negative or lower than what is already reserved.</summary>
    public static bool CanAdjust(int onHand, int reserved, int delta) => onHand + delta >= Math.Max(0, reserved);

    /// <summary>A reservation needs enough stock left after the reservations of other references.</summary>
    public static bool CanReserve(int onHand, int reservedByOthers, int quantity) => quantity > 0 && Available(onHand, reservedByOthers) >= quantity;

    public static bool IsLow(bool trackInventory, int onHand, int threshold) => trackInventory && onHand <= threshold;
}

public sealed record StockLevel(int OnHand, int Reserved)
{
    public int Available => StockRules.Available(OnHand, Reserved);
}

public sealed class StockMovement
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int? CombinationId { get; set; }
    public int Delta { get; set; }
    public int QuantityAfter { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public int? ActorCustomerId { get; set; }
    public DateTime CreatedOnUtc { get; set; }
}

public enum InventoryOutcome
{
    Ok = 0,
    NotFound = 1,
    Insufficient = 2,
    Expired = 3
}

/// <summary>Result of a store operation; <see cref="OnHand"/> is the quantity after a successful change.</summary>
public sealed record InventoryChange(InventoryOutcome Outcome, int OnHand = 0)
{
    public bool Succeeded => Outcome == InventoryOutcome.Ok;
}

public sealed record StockAdjustment(
    int ProductId, int? CombinationId, int Delta, string Reason, string? Reference, string? Note, int? ActorCustomerId);

public sealed record StockReservationRequest(
    string Reference, int ProductId, int? CombinationId, int Quantity, DateTime ExpiresOnUtc);

public interface IInventoryStore
{
    /// <summary>On hand and reserved for the product (all its combinations together) or one combination. Null when it does not exist.</summary>
    Task<StockLevel?> GetLevelAsync(int productId, int? combinationId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Levels per combination id.</summary>
    Task<IReadOnlyDictionary<int, StockLevel>> GetCombinationLevelsAsync(int productId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Atomically changes on hand and writes a ledger row. Refused (<see cref="InventoryOutcome.Insufficient"/>) when <see cref="StockRules.CanAdjust"/> fails.</summary>
    Task<InventoryChange> AdjustAsync(StockAdjustment adjustment, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Writes a ledger row only, for a change the caller already stored (the initial stock of a new product).</summary>
    Task RecordMovementAsync(StockMovement movement, CancellationToken cancellationToken);

    /// <summary>Holds stock for a reference. Idempotent per reference, product and combination: a repeat replaces quantity and expiry.</summary>
    Task<InventoryChange> ReserveAsync(StockReservationRequest request, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Closes the active reservations of a reference without a sale. Returns how many were closed.</summary>
    Task<int> ReleaseAsync(string reference, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Turns every active reservation of a reference into a sale in one transaction, or changes nothing.</summary>
    Task<InventoryChange> CommitAsync(string reference, DateTime nowUtc, CancellationToken cancellationToken);

    Task<bool> HasActiveReservationsAsync(int productId, DateTime nowUtc, CancellationToken cancellationToken);

    Task<(IReadOnlyList<StockMovement> Items, int TotalCount)> GetMovementsAsync(int productId, int page, int pageSize, CancellationToken cancellationToken);

    Task SetSettingsAsync(int productId, bool trackInventory, int lowStockThreshold, CancellationToken cancellationToken);
}

public sealed record CombinationStock(int Id, string? Sku, string AttributesJson, StockLevel Level);

public sealed record InventoryOverview(
    bool TrackInventory, int LowStockThreshold, StockLevel Product, IReadOnlyList<CombinationStock> Combinations);

/// <summary>What a customer can buy now: the product (or all variants together) and each combination.</summary>
public sealed record Availability(bool TrackInventory, int Product, IReadOnlyDictionary<int, int> Combinations);

public sealed record AdjustStockCommand(int? CombinationId, int Delta, string? Reason, string? Note);

public interface IInventoryService
{
    // ---- Seller: one shop at a time; products of other shops do not exist for the caller ----

    Task<CatalogResult<InventoryOverview>> GetOverviewForVendorAsync(int vendorId, int productId, CancellationToken cancellationToken);
    Task<CatalogResult<InventoryOverview>> AdjustForVendorAsync(int vendorId, int productId, AdjustStockCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<InventoryOverview>> SetSettingsForVendorAsync(int vendorId, int productId, bool trackInventory, int lowStockThreshold, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<PagedResult<StockMovement>>> GetMovementsForVendorAsync(int vendorId, int productId, int page, int pageSize, CancellationToken cancellationToken);

    // ---- Internal: used by the cart and checkout ----

    /// <summary>Holds stock for <paramref name="reference"/> (for example a cart id). Products that do not track inventory succeed without holding anything.</summary>
    Task<CatalogResult<bool>> ReserveAsync(string reference, int productId, int? combinationId, int quantity, int? minutes, CancellationToken cancellationToken);

    Task<int> ReleaseAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Turns the held stock of a reference into a sale.</summary>
    Task<CatalogResult<bool>> CommitAsync(string reference, CancellationToken cancellationToken);

    // ---- Public ----

    Task<Availability?> GetAvailabilityAsync(int productId, CancellationToken cancellationToken);
}
