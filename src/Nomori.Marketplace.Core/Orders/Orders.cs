using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Orders;

public static class OrderLimits
{
    public const int MaxShops = 20;
    public const int MaxLinesPerShop = 50;
    public const int MaxQuantity = 10_000;
    public const decimal MaxAmount = 1_000_000_000m;
    public const int MaxKeyLength = 100;
    public const int MaxReasonLength = 500;
    public const int MaxCarrierLength = 100;
    public const int MaxTrackingLength = 100;
    public const int MaxNoteLength = 500;
}

/// <summary>Business-rule codes of orders (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class OrderErrors
{
    public const string InvalidTransition = "order.invalid_transition";
    public const string PlacementConflict = "order.placement_conflict";
}

/// <summary>The stored value is part of the database contract: add at the end, never renumber.</summary>
public enum ShopOrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Shipped = 2,
    Delivered = 3,
    Completed = 4,
    Cancelled = 5
}

public enum OrderActor
{
    Customer = 0,
    Shop = 1,
    Admin = 2,
    System = 3
}

public enum OrderAction
{
    Confirm = 0,
    Ship = 1,
    Deliver = 2,
    Complete = 3,
    Cancel = 4
}

/// <summary>The status of a whole order, derived from its shop orders. It is never stored.</summary>
public enum OverallOrderStatus
{
    Processing = 0,
    Completed = 1,
    Cancelled = 2
}

public sealed class Order
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public string PlacementKey { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal ShippingTotal { get; set; }
    public decimal Total { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public string? CustomerNote { get; set; }

    // The recipient as written at the time of the order. Never linked to the address book.
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientPhone { get; set; } = string.Empty;
    public string Address1 { get; set; } = string.Empty;
    public string? Address2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string? StateProvince { get; set; }
    public string? PostalCode { get; set; }
    public string CountryCode { get; set; } = string.Empty;

    public DateTime CreatedOnUtc { get; set; }

    /// <summary>Filled by reads that ask for them.</summary>
    public List<ShopOrder> ShopOrders { get; set; } = [];
}

public sealed class ShopOrder
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string Number { get; set; } = string.Empty;
    public int VendorId { get; set; }
    public string ShopName { get; set; } = string.Empty;
    public ShopOrderStatus Status { get; set; }
    public decimal Subtotal { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal Total { get; set; }
    public string ShippingMethodName { get; set; } = string.Empty;
    public int? ShippingRateId { get; set; }
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }

    /// <summary>Units in all lines; filled by list reads, which do not load the lines.</summary>
    public int ItemCount { get; set; }

    public List<OrderLine> Lines { get; set; } = [];

    /// <summary>The order header (recipient, currency, payment method); filled by shop order reads.</summary>
    public Order? Order { get; set; }
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int ShopOrderId { get; set; }
    public int ProductId { get; set; }
    public int? CombinationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? VariantLabel { get; set; }
    public string? Sku { get; set; }
    public int PictureId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed record OrderHistoryEntry(
    int Id, int ShopOrderId, ShopOrderStatus? FromStatus, ShopOrderStatus ToStatus, OrderActor Actor, int? ActorCustomerId, string? Note, DateTime CreatedOnUtc);

// ---- Commands ----

public sealed record NewOrderRecipient(
    string? Name, string? Phone, string? Address1, string? Address2, string? City, string? StateProvince, string? PostalCode, string? CountryCode);

public sealed record NewOrderLine(
    int ProductId, int? CombinationId, string? Name, string? VariantLabel, string? Sku, int PictureId, int Quantity, decimal UnitPrice);

public sealed record NewShopOrder(
    int VendorId, string? ShippingMethodName, int? ShippingRateId, decimal ShippingFee, IReadOnlyList<NewOrderLine>? Lines);

/// <summary>What checkout decided. Totals are not part of it: the service computes them.</summary>
public sealed record NewOrderCommand(
    int CustomerId, string? PlacementKey, string? PaymentMethod, string? CustomerNote, NewOrderRecipient? Recipient,
    IReadOnlyList<NewShopOrder>? Shops);

public sealed record ShipCommand(string? Carrier, string? TrackingNumber);

public sealed record OrderListQuery(
    int? CustomerId, int? VendorId, ShopOrderStatus? Status, string? Search, DateTime? FromUtc, DateTime? ToUtc, int Page, int PageSize);

/// <summary>One change of a shop order, applied together with its history row.</summary>
public sealed record ShopOrderTransition(
    int ShopOrderId, ShopOrderStatus Expected, ShopOrderStatus Target, OrderActor Actor, int? ActorCustomerId, string? Note,
    string? Carrier, string? TrackingNumber, string? CancelReason, DateTime NowUtc);

public sealed record OrderDetail(Order Order, IReadOnlyDictionary<int, IReadOnlyList<OrderHistoryEntry>> History);

public sealed record ShopOrderDetail(ShopOrder ShopOrder, IReadOnlyList<OrderHistoryEntry> History);

// ---- Rules ----

public static class OrderRules
{
    public static bool IsFinal(ShopOrderStatus status) => status is ShopOrderStatus.Completed or ShopOrderStatus.Cancelled;

    /// <summary>
    /// The status an action leads to, or null when this actor may not do it from this status.
    /// Shops cancel before shipping, customers only before the shop confirmed, administrators until the end.
    /// </summary>
    public static ShopOrderStatus? Transition(ShopOrderStatus from, OrderAction action, OrderActor actor) => (action, from, actor) switch
    {
        (OrderAction.Confirm, ShopOrderStatus.Pending, OrderActor.Shop) => ShopOrderStatus.Confirmed,
        (OrderAction.Ship, ShopOrderStatus.Confirmed, OrderActor.Shop) => ShopOrderStatus.Shipped,
        (OrderAction.Deliver, ShopOrderStatus.Shipped, OrderActor.Shop or OrderActor.Customer) => ShopOrderStatus.Delivered,
        (OrderAction.Complete, ShopOrderStatus.Delivered, OrderActor.System) => ShopOrderStatus.Completed,

        (OrderAction.Cancel, ShopOrderStatus.Pending or ShopOrderStatus.Confirmed, OrderActor.Shop) => ShopOrderStatus.Cancelled,
        (OrderAction.Cancel, ShopOrderStatus.Pending, OrderActor.Customer or OrderActor.System) => ShopOrderStatus.Cancelled,
        (OrderAction.Cancel, ShopOrderStatus.Pending or ShopOrderStatus.Confirmed or ShopOrderStatus.Shipped or ShopOrderStatus.Delivered, OrderActor.Admin) => ShopOrderStatus.Cancelled,
        _ => null
    };

    /// <summary>Processing while a shop order is not finished; otherwise completed when at least one was completed, cancelled when none was.</summary>
    public static OverallOrderStatus Overall(IEnumerable<ShopOrderStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Any(s => !IsFinal(s))) return OverallOrderStatus.Processing;
        return list.Any(s => s == ShopOrderStatus.Completed) ? OverallOrderStatus.Completed : OverallOrderStatus.Cancelled;
    }

    public static decimal LineTotal(decimal unitPrice, int quantity, int decimalPlaces) =>
        Math.Round(unitPrice * quantity, decimalPlaces, MidpointRounding.AwayFromZero);

    public static string NumberFor(DateTime createdOnUtc, int orderId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"NM{createdOnUtc:yyMMdd}-{orderId:D4}");

    /// <summary>Shop orders are numbered from 1 in the order the shops were given.</summary>
    public static string ShopNumberFor(string orderNumber, int position) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{orderNumber}-{position}");

    public static string ToWire(ShopOrderStatus status) => status switch
    {
        ShopOrderStatus.Pending => "pending",
        ShopOrderStatus.Confirmed => "confirmed",
        ShopOrderStatus.Shipped => "shipped",
        ShopOrderStatus.Delivered => "delivered",
        ShopOrderStatus.Completed => "completed",
        _ => "cancelled"
    };

    public static bool TryParseWire(string? value, out ShopOrderStatus status)
    {
        foreach (var candidate in Enum.GetValues<ShopOrderStatus>())
        {
            if (string.Equals(ToWire(candidate), value, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }
        status = default;
        return false;
    }

    public static string ToWire(OrderActor actor) => actor switch
    {
        OrderActor.Customer => "customer",
        OrderActor.Shop => "shop",
        OrderActor.Admin => "admin",
        _ => "system"
    };

    public static OrderActor ParseActor(string value) => value switch
    {
        "customer" => OrderActor.Customer,
        "shop" => OrderActor.Shop,
        "admin" => OrderActor.Admin,
        _ => OrderActor.System
    };

    public static string ToWire(OverallOrderStatus status) => status switch
    {
        OverallOrderStatus.Processing => "processing",
        OverallOrderStatus.Completed => "completed",
        _ => "cancelled"
    };
}

// ---- Store and service ----

public interface IOrderStore
{
    /// <summary>
    /// Writes the order, its shop orders and lines, the first history rows and the numbers in one transaction. When the placement key is
    /// already used nothing is written and the order that used it comes back with <c>Created = false</c>.
    /// </summary>
    Task<(Order Order, bool Created)> InsertAsync(Order order, CancellationToken cancellationToken);

    /// <summary>The order with its shop orders and their lines.</summary>
    Task<Order?> GetOrderAsync(int id, CancellationToken cancellationToken);

    /// <summary>A shop order with its lines and its order header.</summary>
    Task<ShopOrder?> GetShopOrderAsync(int id, CancellationToken cancellationToken);

    /// <summary>Orders with their shop orders (no lines), newest first. The filters apply to the shop orders they contain.</summary>
    Task<PagedResult<Order>> GetOrdersAsync(OrderListQuery query, CancellationToken cancellationToken);

    /// <summary>Shop orders with their order header (no lines), newest first.</summary>
    Task<PagedResult<ShopOrder>> GetShopOrdersAsync(OrderListQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<ShopOrderStatus, int>> CountByStatusAsync(int vendorId, CancellationToken cancellationToken);

    /// <summary>Compare-and-set of the status plus its history row. False means the shop order was no longer in the expected status.</summary>
    Task<bool> TryTransitionAsync(ShopOrderTransition transition, CancellationToken cancellationToken);

    /// <summary>Changes carrier and tracking while the shop order is still shipped, and records it in the history. False otherwise.</summary>
    Task<bool> TryUpdateTrackingAsync(int shopOrderId, string carrier, string trackingNumber, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderHistoryEntry>> GetHistoryAsync(IReadOnlyCollection<int> shopOrderIds, CancellationToken cancellationToken);
}

public interface IOrderService
{
    // ---- Checkout ----

    /// <summary>Creates the order from what checkout decided and computes every total. The same placement key returns the same order.</summary>
    Task<CatalogResult<Order>> CreateAsync(NewOrderCommand command, CancellationToken cancellationToken);

    // ---- Customers ----

    Task<PagedResult<Order>> GetMyOrdersAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken);
    Task<CatalogResult<OrderDetail>> GetMyOrderAsync(int customerId, int orderId, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> CancelAsCustomerAsync(int customerId, int shopOrderId, string? reason, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> ConfirmReceiptAsync(int customerId, int shopOrderId, CancellationToken cancellationToken);

    // ---- Shop members (the caller has already been checked against the shop) ----

    Task<PagedResult<ShopOrder>> GetShopOrdersAsync(int vendorId, OrderListQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<ShopOrderStatus, int>> GetCountsAsync(int vendorId, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> GetShopOrderAsync(int vendorId, int shopOrderId, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> ConfirmAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> ShipAsync(int vendorId, int shopOrderId, ShipCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> UpdateTrackingAsync(int vendorId, int shopOrderId, ShipCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> DeliverAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> CancelAsShopAsync(int vendorId, int shopOrderId, string? reason, int actorCustomerId, CancellationToken cancellationToken);

    // ---- Administrators ----

    Task<PagedResult<Order>> GetOrdersAsync(OrderListQuery query, CancellationToken cancellationToken);
    Task<CatalogResult<OrderDetail>> GetOrderAsync(int orderId, CancellationToken cancellationToken);
    Task<CatalogResult<ShopOrderDetail>> CancelAsAdminAsync(int shopOrderId, string? reason, int actorCustomerId, CancellationToken cancellationToken);
}
