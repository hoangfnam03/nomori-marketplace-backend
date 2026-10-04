using Nomori.Marketplace.Core.Cart;

namespace Nomori.Marketplace.Core.Orders;

/// <summary>Outcome of an order operation: field errors (400), an error code (404, 409), or a value.</summary>
public sealed record OrderResult<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors, string? ErrorCode = null)
{
    public bool Succeeded => Errors.Count == 0 && ErrorCode is null;
}

public static class OrderResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    public static OrderResult<T> Success<T>(T value) => new(value, NoErrors);

    public static OrderResult<T> Failure<T>(string field, string message) =>
        new(default, new Dictionary<string, string[]> { [field] = [message] });

    public static OrderResult<T> Failure<T>(IReadOnlyDictionary<string, string[]> errors) => new(default, errors);

    public static OrderResult<T> Error<T>(string errorCode) => new(default, NoErrors, errorCode);
}

// ---- Checkout ----

public sealed record CheckoutShopGroup(
    int VendorId, string? VendorName, IReadOnlyList<CartLineView> Lines, decimal ItemsTotal, decimal ShippingFee, decimal Total);

/// <summary>
/// What placing an order would create right now. Lines keep the cart's issues; <see cref="CanPlace"/> is false while any
/// selected line has a blocking issue, a selected line is missing from the cart, or a limit is exceeded.
/// </summary>
public sealed record CheckoutPreview(
    string CurrencyCode,
    IReadOnlyList<CheckoutShopGroup> Groups,
    decimal ItemsTotal,
    decimal ShippingTotal,
    decimal Total,
    bool CanPlace,
    IReadOnlyList<int> MissingCartItemIds,
    /// <summary>Cart items of shops the customer belongs to; they cannot be bought.</summary>
    IReadOnlyList<int> OwnShopCartItemIds);

public sealed record PlaceOrderCommand(
    IReadOnlyList<int> CartItemIds,
    int AddressId,
    PaymentMethod? PaymentMethod,
    /// <summary>Note for each shop, by vendor id.</summary>
    IReadOnlyDictionary<int, string?>? Notes,
    /// <summary>The total the customer saw. The order is refused when the server's total differs.</summary>
    decimal ExpectedTotal,
    /// <summary>Sent again with a retried request, so a repeat returns the first order instead of a second one.</summary>
    string? IdempotencyKey);

/// <summary>The order placed, and whether this request created it or found the one an earlier identical request created.</summary>
public sealed record PlacedOrder(CustomerOrder Order, bool Created);

// ---- Customer actions ----

/// <summary>A status change. Shipping needs <see cref="Carrier"/> and <see cref="TrackingNumber"/>; sending them for an order already shipped corrects them.</summary>
public sealed record ChangeStoreOrderStatusCommand(
    StoreOrderStatus? Status, string? Reason, string? Note, string? Carrier = null, string? TrackingNumber = null);

/// <summary>Who is acting on a shop order: its customer, a member of its shop, or an administrator.</summary>
public sealed record OrderCaller(int CustomerId, bool IsAdmin, int? MemberVendorId);

public sealed record BulkConfirmResult(IReadOnlyList<int> Confirmed, IReadOnlyList<int> Skipped);

public sealed record AutomationResult(int Cancelled, int Delivered, int Completed);

public sealed record ReorderFailure(int ProductId, string ProductName, string Reason);

public sealed record ReorderResult(IReadOnlyList<int> AddedProductIds, IReadOnlyList<ReorderFailure> Failed, int CartCount);

// ---- Store ----

public enum PlaceOrderOutcome
{
    Created = 0,
    /// <summary>An order with the same idempotency key exists; <see cref="PlaceOrderStoreResult.OrderId"/> is that order.</summary>
    Duplicate = 1,
    InsufficientStock = 2,
    /// <summary>A selected cart item disappeared while the order was being placed (another tab placed it, or it was removed).</summary>
    CartItemMissing = 3
}

public sealed record PlaceOrderStoreResult(PlaceOrderOutcome Outcome, int OrderId = 0, int? ProductId = null);

/// <summary>
/// A shop order changing status. Applied only while the shop order still has <see cref="From"/>, so two people acting at once
/// cannot both succeed. A cancellation puts back the stock its lines took.
/// </summary>
public sealed record StoreOrderTransition(
    int StoreOrderId,
    StoreOrderStatus From,
    StoreOrderStatus To,
    OrderActorType ActorType,
    int? ActorCustomerId,
    string? Reason,
    string? Note,
    PaymentStatus? NewPaymentStatus,
    DateTime NowUtc,
    /// <summary>Put the stock of the lines back (a cancellation before shipping).</summary>
    bool Restock = false,
    string? Carrier = null,
    string? TrackingNumber = null);

public interface IOrderStore
{
    /// <summary>
    /// In one transaction: takes the stock of tracked lines, numbers the order, inserts the order, its shop orders, lines and first
    /// events, and deletes the cart items. Either all of it happens or nothing does. The order's numbers are filled in on success.
    /// </summary>
    Task<PlaceOrderStoreResult> PlaceAsync(CustomerOrder order, IReadOnlyList<int> cartItemIds, CancellationToken cancellationToken);

    Task<int?> FindIdByIdempotencyKeyAsync(int customerId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The order with its shop orders, lines and events; null when it does not exist.</summary>
    Task<CustomerOrder?> GetAsync(int orderId, CancellationToken cancellationToken);

    /// <summary>The shop order with its lines, plus the customer who placed it.</summary>
    Task<(StoreOrder StoreOrder, int CustomerId)?> GetStoreOrderAsync(int storeOrderId, CancellationToken cancellationToken);

    Task<StoreOrderPage> ListForCustomerAsync(StoreOrderQuery query, CancellationToken cancellationToken);

    /// <summary>The shop's orders with the recipient's name and phone, newest first, and the count of every tab.</summary>
    Task<StoreOrderPage> ListForVendorAsync(VendorOrderQuery query, CancellationToken cancellationToken);

    /// <summary>False when the shop order no longer has the expected status.</summary>
    Task<bool> TransitionAsync(StoreOrderTransition transition, CancellationToken cancellationToken);

    /// <summary>Corrects the carrier and tracking number of a shipped order, with an event. False when it is no longer shipped.</summary>
    Task<bool> UpdateShipmentAsync(int storeOrderId, string carrier, string trackingNumber, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Shop orders in <paramref name="status"/> whose deadline for the next automatic step has passed, oldest first.</summary>
    Task<IReadOnlyList<int>> FindDueAsync(StoreOrderStatus status, DateTime dueBeforeUtc, int take, CancellationToken cancellationToken);
}

// ---- Services ----

public interface ICheckoutService
{
    Task<OrderResult<CheckoutPreview>> PreviewAsync(int customerId, IReadOnlyList<int> cartItemIds, CancellationToken cancellationToken);
    Task<OrderResult<PlacedOrder>> PlaceAsync(int customerId, PlaceOrderCommand command, CancellationToken cancellationToken);
}

public interface IOrderService
{
    Task<StoreOrderPage> ListForCustomerAsync(StoreOrderQuery query, CancellationToken cancellationToken);

    /// <summary>The customer's own order; anyone else's is not found.</summary>
    Task<OrderResult<CustomerOrder>> GetForCustomerAsync(int customerId, int orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Changes a shop order's status as its customer, a member of its shop or an administrator, each with their own transitions.
    /// Anyone else gets not found. Returns the order as the caller may see it.
    /// </summary>
    Task<OrderResult<CustomerOrder>> ChangeStatusAsync(int storeOrderId, ChangeStoreOrderStatusCommand command, OrderCaller caller, CancellationToken cancellationToken);

    Task<StoreOrderPage> ListForVendorAsync(VendorOrderQuery query, CancellationToken cancellationToken);

    /// <summary>The order with only the shop's own part in it: other shops' parts of the order are never shown to a shop.</summary>
    Task<OrderResult<CustomerOrder>> GetForVendorAsync(int vendorId, int storeOrderId, CancellationToken cancellationToken);

    /// <summary>Confirms several pending orders of a shop; the ones not pending (or not the shop's) are skipped.</summary>
    Task<OrderResult<BulkConfirmResult>> ConfirmManyAsync(int vendorId, IReadOnlyList<int> storeOrderIds, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Adds the lines of a shop order back to the cart at today's prices.</summary>
    Task<OrderResult<ReorderResult>> ReorderAsync(int customerId, int storeOrderId, CancellationToken cancellationToken);
}

/// <summary>The scheduled steps of the lifecycle (vendor-orders-prd.md, FR-08). Safe to run again: each step only moves orders still due.</summary>
public interface IOrderAutomation
{
    Task<AutomationResult> RunAsync(CancellationToken cancellationToken);
}
