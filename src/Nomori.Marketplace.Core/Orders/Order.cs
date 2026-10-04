namespace Nomori.Marketplace.Core.Orders;

/// <summary>Lifecycle of a shop's part of an order. See docs/modules/vendor-orders-prd.md, section 5.</summary>
public enum StoreOrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Shipped = 2,
    Delivered = 3,
    Completed = 4,
    Cancelled = 5
}

public enum PaymentMethod
{
    CashOnDelivery = 0
}

/// <summary>Payment of one shop order. Cash on delivery is paid when the parcel is delivered and voided when the order is cancelled first.</summary>
public enum PaymentStatus
{
    Pending = 0,
    Paid = 1,
    Voided = 2,
    Refunded = 3
}

/// <summary>Who changed a shop order. The customer only ever sees the role, never a name.</summary>
public enum OrderActorType
{
    Customer = 0,
    Vendor = 1,
    Admin = 2,
    System = 3
}

/// <summary>The overall status of an order, derived from its shop orders (customer-orders-prd.md, section 6.2). Never stored.</summary>
public enum OrderOverallStatus
{
    Processing = 0,
    Delivered = 1,
    Cancelled = 2
}

public sealed class CustomerOrder
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public int CustomerId { get; set; }

    /// <summary>The primary currency when the order was placed. Every amount of the order is in it.</summary>
    public string CurrencyCode { get; set; } = string.Empty;

    public decimal ItemsTotal { get; set; }
    public decimal ShippingTotal { get; set; }
    public decimal Total { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public OrderAddress ShippingAddress { get; set; } = new();
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime CreatedOnUtc { get; set; }
    public List<StoreOrder> StoreOrders { get; set; } = [];

    public OrderOverallStatus OverallStatus => OrderRules.OverallStatus(StoreOrders.Select(s => s.Status));
}

/// <summary>A copy of the delivery address taken when the order was placed; later edits of the address book do not change it.</summary>
public sealed class OrderAddress
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string Address1 { get; set; } = string.Empty;
    public string? Address2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string? StateProvince { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string? ZipPostalCode { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
}

public sealed class StoreOrder
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int VendorId { get; set; }
    public string? VendorName { get; set; }

    /// <summary><c>&lt;order number&gt;-&lt;n&gt;</c>, n counting the shops of the order from 1.</summary>
    public string SubOrderNumber { get; set; } = string.Empty;

    public StoreOrderStatus Status { get; set; }
    public PaymentStatus PaymentStatus { get; set; }
    public decimal ItemsTotal { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal Total { get; set; }
    public string? CustomerNote { get; set; }
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }

    /// <summary>The shop must confirm before this moment or the order is cancelled automatically.</summary>
    public DateTime ConfirmByUtc { get; set; }

    public DateTime? ConfirmedOnUtc { get; set; }
    public DateTime? ShippedOnUtc { get; set; }
    public DateTime? DeliveredOnUtc { get; set; }
    public DateTime? CompletedOnUtc { get; set; }
    public DateTime? CancelledOnUtc { get; set; }
    public string? CancelReason { get; set; }
    public string? CancelNote { get; set; }
    public OrderActorType? CancelledBy { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public List<StoreOrderEvent> Events { get; set; } = [];

    /// <summary>Filled when the shop order is listed on its own, so the card can show the order it belongs to.</summary>
    public string? OrderNumber { get; set; }

    /// <summary>Filled in the shop's list, which shows who receives the parcel without loading the whole order.</summary>
    public string? RecipientName { get; set; }

    public string? RecipientPhone { get; set; }
}

/// <summary>One product (or one variant choice) bought, as it was when the order was placed.</summary>
public sealed class OrderItem
{
    public int Id { get; set; }
    public int StoreOrderId { get; set; }
    public int ProductId { get; set; }
    public int? CombinationId { get; set; }

    /// <summary>The variant value ids of the cart line, to buy the same choice again.</summary>
    public string ValueIds { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;
    public string? VariantDescription { get; set; }
    public string? Sku { get; set; }
    public int PictureId { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>True when stock was taken for this line, so a cancellation knows to put it back.</summary>
    public bool StockDeducted { get; set; }
}

public sealed class StoreOrderEvent
{
    public int Id { get; set; }
    public int StoreOrderId { get; set; }
    public StoreOrderStatus? FromStatus { get; set; }
    public StoreOrderStatus ToStatus { get; set; }
    public OrderActorType ActorType { get; set; }
    public int? ActorCustomerId { get; set; }
    public string? Reason { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedOnUtc { get; set; }
}

public sealed class OrderOptions
{
    public const string SectionName = "Orders";

    /// <summary>Shipping fee of every shop order until shipping zones exist (F20). In the primary currency.</summary>
    public decimal ShippingFeePerStoreOrder { get; init; }

    /// <summary>A shop order whose items total reaches this ships free. Null: never free (unless the fee is 0).</summary>
    public decimal? FreeShippingThreshold { get; init; }

    /// <summary>Hours a shop has to confirm a new order.</summary>
    public int ConfirmWithinHours { get; init; } = 48;

    /// <summary>A shipped order the customer never confirmed counts as delivered after this many days.</summary>
    public int AutoDeliverAfterDays { get; init; } = 7;

    /// <summary>A delivered order is completed (open for settlement) after this many days without a complaint.</summary>
    public int AutoCompleteAfterDays { get; init; } = 7;

    /// <summary>The scheduled jobs (cancel unconfirmed, deliver, complete) run in the API until background jobs exist (F29).</summary>
    public bool AutomationEnabled { get; init; } = true;

    public int AutomationIntervalMinutes { get; init; } = 5;
}

public static class OrderLimits
{
    public const int MaxLines = 50;
    public const int MaxShops = 10;
    public const int MaxNoteLength = 500;
    public const int MaxIdempotencyKeyLength = 100;
    public const int MaxPageSize = 50;
    public const int MaxSearchLength = 100;
    public const int MaxCarrierLength = 100;
    public const int MaxTrackingNumberLength = 100;
    public const int MaxBulkConfirm = 100;
}

/// <summary>Business-rule codes returned in <c>ProblemDetails.detail</c> (409). Not found and forbidden map to 404 and 403.</summary>
public static class OrderErrors
{
    public const string NotFound = "not_found";
    public const string TotalChanged = "order.total_changed";
    public const string ItemsUnavailable = "order.items_unavailable";
    public const string CartItemNotFound = "order.cart_item_not_found";
    public const string AddressInvalid = "order.address_invalid";
    public const string InvalidTransition = "store_order.invalid_transition";
    public const string ConcurrentUpdate = "store_order.concurrent_update";
}

/// <summary>Why a customer cancels. <see cref="Other"/> needs a note.</summary>
public static class CustomerCancelReasons
{
    public const string ChangeAddress = "change_address";
    public const string ChangeItems = "change_items";
    public const string ChangedMind = "changed_mind";
    public const string BetterPrice = "better_price";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { ChangeAddress, ChangeItems, ChangedMind, BetterPrice, Other };
}

/// <summary>Why a shop cancels (vendor-orders-prd.md, US-B5). <see cref="Other"/> needs a note.</summary>
public static class VendorCancelReasons
{
    public const string OutOfStock = "out_of_stock";
    public const string CannotContact = "cannot_contact";
    public const string WrongPrice = "wrong_price";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { OutOfStock, CannotContact, WrongPrice, Other };
}

/// <summary>Reasons recorded by actors that do not choose one.</summary>
public static class SystemCancelReasons
{
    /// <summary>The shop did not confirm before <see cref="StoreOrder.ConfirmByUtc"/>.</summary>
    public const string NotConfirmedInTime = "not_confirmed_in_time";

    /// <summary>An administrator cancelled; the note says why.</summary>
    public const string Admin = "admin";

    /// <summary>A shipped order's tracking details were corrected (an event, not a cancellation).</summary>
    public const string TrackingUpdated = "tracking_updated";
}

public static class OrderRules
{
    public static OrderOverallStatus OverallStatus(IEnumerable<StoreOrderStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Count == 0 || list.All(s => s == StoreOrderStatus.Cancelled)) return OrderOverallStatus.Cancelled;
        return list.All(s => s is StoreOrderStatus.Delivered or StoreOrderStatus.Completed or StoreOrderStatus.Cancelled)
            ? OrderOverallStatus.Delivered
            : OrderOverallStatus.Processing;
    }

    /// <summary>The fee of one shop order under <see cref="OrderOptions"/>.</summary>
    public static decimal ShippingFee(decimal itemsTotal, OrderOptions options) =>
        options.FreeShippingThreshold is { } threshold && itemsTotal >= threshold ? 0m : options.ShippingFeePerStoreOrder;

    /// <summary><c>NM&lt;yyMMdd&gt;-&lt;nnnn&gt;</c>, the sequence restarting every UTC day.</summary>
    public static string OrderNumber(DateTime utcDay, int sequence) =>
        $"NM{utcDay:yyMMdd}-{sequence.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)}";

    public static string SubOrderNumber(string orderNumber, int shopIndex) =>
        $"{orderNumber}-{shopIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>What a customer may do to their own shop order: cancel while pending, confirm receipt once shipped.</summary>
    public static bool CustomerMay(StoreOrderStatus from, StoreOrderStatus to) =>
        (from, to) is (StoreOrderStatus.Pending, StoreOrderStatus.Cancelled) or (StoreOrderStatus.Shipped, StoreOrderStatus.Delivered);

    /// <summary>What a shop member may do (vendor-orders-prd.md, section 5 and FR-07). Cancelling is refused once shipped.</summary>
    public static bool VendorMay(StoreOrderStatus from, StoreOrderStatus to) => (from, to) is
        (StoreOrderStatus.Pending, StoreOrderStatus.Confirmed) or
        (StoreOrderStatus.Pending, StoreOrderStatus.Cancelled) or
        (StoreOrderStatus.Confirmed, StoreOrderStatus.Shipped) or
        (StoreOrderStatus.Confirmed, StoreOrderStatus.Cancelled) or
        (StoreOrderStatus.Shipped, StoreOrderStatus.Delivered);

    /// <summary>An administrator may cancel any shop order that is not finished yet (US-D1).</summary>
    public static bool AdminMayCancel(StoreOrderStatus from) => from is not (StoreOrderStatus.Completed or StoreOrderStatus.Cancelled);

    /// <summary>Stock goes back only when the parcel never left the shop. After shipping the goods are with the carrier or the customer.</summary>
    public static bool RestocksOnCancel(StoreOrderStatus from) => from is StoreOrderStatus.Pending or StoreOrderStatus.Confirmed;

    /// <summary>A cancelled shop order owes nothing; one already paid must be refunded.</summary>
    public static PaymentStatus PaymentAfterCancel(PaymentStatus current) =>
        current is PaymentStatus.Paid ? PaymentStatus.Refunded : PaymentStatus.Voided;
}

/// <summary>A page of a customer's shop orders, with the count of every tab.</summary>
public sealed record StoreOrderPage(
    IReadOnlyList<StoreOrder> Items, int TotalCount, int Page, int PageSize, IReadOnlyDictionary<string, int> TabCounts);

/// <summary>Tabs of "My orders" (customer-orders-prd.md, section 6.3).</summary>
public static class OrderTabs
{
    public const string All = "all";
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> Ordered = [All, Pending, Confirmed, Shipped, Delivered, Cancelled];

    /// <summary>The statuses a tab shows; null for every status.</summary>
    public static IReadOnlyList<StoreOrderStatus>? Statuses(string tab) => tab switch
    {
        Pending => [StoreOrderStatus.Pending],
        Confirmed => [StoreOrderStatus.Confirmed],
        Shipped => [StoreOrderStatus.Shipped],
        Delivered => [StoreOrderStatus.Delivered, StoreOrderStatus.Completed],
        Cancelled => [StoreOrderStatus.Cancelled],
        _ => null
    };
}

public sealed record StoreOrderQuery(int CustomerId, string Tab, string? Search, int Page, int PageSize);

/// <summary>Tabs of the shop's order list (vendor-orders-prd.md, US-B1): one per status, plus all.</summary>
public static class VendorOrderTabs
{
    public const string All = "all";

    public static readonly IReadOnlyList<string> Ordered = [All, "pending", "confirmed", "shipped", "delivered", "completed", "cancelled"];

    public static IReadOnlyList<StoreOrderStatus>? Statuses(string tab) =>
        Enum.TryParse<StoreOrderStatus>(tab, ignoreCase: true, out var status) && tab != All ? [status] : null;

    public static string Of(StoreOrderStatus status) => status.ToString().ToLowerInvariant();
}

/// <param name="Search">Shop order number, recipient name or recipient phone.</param>
public sealed record VendorOrderQuery(int VendorId, string Tab, string? Search, DateTime? FromUtc, DateTime? ToUtc, int Page, int PageSize);
