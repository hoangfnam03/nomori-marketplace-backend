using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Orders;

/// <summary>
/// Orders after checkout (F18-A, vendor-orders-prd.md): the customer's "My orders", the shop's order list, and the status changes
/// each of them (and an administrator) may make.
/// </summary>
public sealed class OrderService(
    IOrderStore orderStore,
    ICartService cartService,
    ICustomerIdentityStore identityStore,
    IAuditLogService auditLog,
    OrderNotifier notifier,
    IClock clock) : IOrderService
{
    public Task<StoreOrderPage> ListForCustomerAsync(StoreOrderQuery query, CancellationToken cancellationToken)
    {
        var tab = OrderTabs.Ordered.Contains(query.Tab) ? query.Tab : OrderTabs.All;
        return orderStore.ListForCustomerAsync(
            query with { Tab = tab, Search = TrimSearch(query.Search), Page = Math.Max(query.Page, 1), PageSize = Math.Clamp(query.PageSize, 1, OrderLimits.MaxPageSize) },
            cancellationToken);
    }

    public async Task<OrderResult<CustomerOrder>> GetForCustomerAsync(int customerId, int orderId, CancellationToken cancellationToken)
    {
        var order = await orderStore.GetAsync(orderId, cancellationToken);
        // Someone else's order is reported as missing, never as forbidden.
        return order is null || order.CustomerId != customerId
            ? OrderResult.Error<CustomerOrder>(OrderErrors.NotFound)
            : OrderResult.Success(order);
    }

    public Task<StoreOrderPage> ListForVendorAsync(VendorOrderQuery query, CancellationToken cancellationToken)
    {
        var tab = VendorOrderTabs.Ordered.Contains(query.Tab) ? query.Tab : VendorOrderTabs.All;
        return orderStore.ListForVendorAsync(
            query with { Tab = tab, Search = TrimSearch(query.Search), Page = Math.Max(query.Page, 1), PageSize = Math.Clamp(query.PageSize, 1, OrderLimits.MaxPageSize) },
            cancellationToken);
    }

    public async Task<OrderResult<CustomerOrder>> GetForVendorAsync(int vendorId, int storeOrderId, CancellationToken cancellationToken)
    {
        var found = await orderStore.GetStoreOrderAsync(storeOrderId, cancellationToken);
        if (found is not { } pair || pair.StoreOrder.VendorId != vendorId) return OrderResult.Error<CustomerOrder>(OrderErrors.NotFound);

        var order = await orderStore.GetAsync(pair.StoreOrder.OrderId, cancellationToken);
        return order is null ? OrderResult.Error<CustomerOrder>(OrderErrors.NotFound) : OrderResult.Success(OnlyShop(order, vendorId));
    }

    public async Task<OrderResult<CustomerOrder>> ChangeStatusAsync(
        int storeOrderId, ChangeStoreOrderStatusCommand command, OrderCaller caller, CancellationToken cancellationToken)
    {
        var found = await orderStore.GetStoreOrderAsync(storeOrderId, cancellationToken);
        if (found is not { } pair) return OrderResult.Error<CustomerOrder>(OrderErrors.NotFound);
        var storeOrder = pair.StoreOrder;

        // A shop member acts as the shop (a shop cannot buy from itself, so it is never also the customer); then the customer; then an admin.
        OrderActorType? actor =
            caller.MemberVendorId == storeOrder.VendorId ? OrderActorType.Vendor
            : pair.CustomerId == caller.CustomerId ? OrderActorType.Customer
            : caller.IsAdmin ? OrderActorType.Admin
            : null;
        if (actor is not { } role) return OrderResult.Error<CustomerOrder>(OrderErrors.NotFound);

        if (command.Status is not { } target || !Enum.IsDefined(target))
            return OrderResult.Failure<CustomerOrder>("status", "Choose the new status.");

        // A shop corrects the tracking details of an order already shipped by sending "shipped" again.
        if (role == OrderActorType.Vendor && storeOrder.Status == StoreOrderStatus.Shipped && target == StoreOrderStatus.Shipped)
            return await UpdateShipmentAsync(storeOrder, command, caller, role, cancellationToken);

        var allowed = role switch
        {
            OrderActorType.Customer => OrderRules.CustomerMay(storeOrder.Status, target),
            OrderActorType.Vendor => OrderRules.VendorMay(storeOrder.Status, target),
            _ => target == StoreOrderStatus.Cancelled && OrderRules.AdminMayCancel(storeOrder.Status)
        };
        if (!allowed) return OrderResult.Error<CustomerOrder>(OrderErrors.InvalidTransition);

        var (reason, note, carrier, tracking, errors) = ValidateDetails(role, target, command);
        if (errors.Count > 0) return OrderResult.Failure<CustomerOrder>(errors);

        var cancelling = target == StoreOrderStatus.Cancelled;
        PaymentStatus? payment = target switch
        {
            StoreOrderStatus.Cancelled => OrderRules.PaymentAfterCancel(storeOrder.PaymentStatus),
            // Cash on delivery is collected with the parcel.
            StoreOrderStatus.Delivered => PaymentStatus.Paid,
            _ => null
        };
        var applied = await orderStore.TransitionAsync(new StoreOrderTransition(
            storeOrder.Id, storeOrder.Status, target, role, caller.CustomerId, reason, note, payment, clock.UtcNow,
            Restock: cancelling && OrderRules.RestocksOnCancel(storeOrder.Status), Carrier: carrier, TrackingNumber: tracking), cancellationToken);
        // Someone else changed it a moment earlier: the caller sees the new state and decides again.
        if (!applied) return OrderResult.Error<CustomerOrder>(OrderErrors.ConcurrentUpdate);

        await auditLog.WriteAsync(AuditEvent(role, target), caller.CustomerId, entityType: "StoreOrder", entityId: storeOrder.Id,
            details: new { subOrderNumber = storeOrder.SubOrderNumber, from = storeOrder.Status.ToString(), to = target.ToString(), reason },
            cancellationToken: cancellationToken);
        await NotifyAsync(storeOrder, pair.CustomerId, role, target, reason, note, carrier, tracking, cancellationToken);

        var order = (await orderStore.GetAsync(storeOrder.OrderId, cancellationToken))!;
        return OrderResult.Success(role == OrderActorType.Vendor ? OnlyShop(order, storeOrder.VendorId) : order);
    }

    public async Task<OrderResult<BulkConfirmResult>> ConfirmManyAsync(
        int vendorId, IReadOnlyList<int> storeOrderIds, int actorCustomerId, CancellationToken cancellationToken)
    {
        var ids = storeOrderIds.Distinct().ToList();
        if (ids.Count is 0 or > OrderLimits.MaxBulkConfirm)
            return OrderResult.Failure<BulkConfirmResult>("storeOrderIds", $"Choose between 1 and {OrderLimits.MaxBulkConfirm} orders.");

        var caller = new OrderCaller(actorCustomerId, IsAdmin: false, MemberVendorId: vendorId);
        var confirmed = new List<int>();
        var skipped = new List<int>();
        foreach (var id in ids)
        {
            var result = await ChangeStatusAsync(id, new ChangeStoreOrderStatusCommand(StoreOrderStatus.Confirmed, null, null), caller, cancellationToken);
            (result.Succeeded ? confirmed : skipped).Add(id);
        }
        return OrderResult.Success(new BulkConfirmResult(confirmed, skipped));
    }

    public async Task<OrderResult<ReorderResult>> ReorderAsync(int customerId, int storeOrderId, CancellationToken cancellationToken)
    {
        var found = await orderStore.GetStoreOrderAsync(storeOrderId, cancellationToken);
        if (found is not { } pair || pair.CustomerId != customerId) return OrderResult.Error<ReorderResult>(OrderErrors.NotFound);

        var added = new List<int>();
        var failed = new List<ReorderFailure>();
        foreach (var item in pair.StoreOrder.Items)
        {
            // The cart checks visibility, stock, price and the own-shop rule exactly as when adding from the product page.
            var result = await cartService.AddAsync(customerId,
                new AddToCartCommand(item.ProductId, item.Quantity, CartRules.ParseValueKey(item.ValueIds)), cancellationToken);
            if (result.Succeeded) added.Add(item.ProductId);
            else failed.Add(new ReorderFailure(item.ProductId, item.ProductName,
                result.ErrorCode ?? result.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "unavailable"));
        }

        return OrderResult.Success(new ReorderResult(added, failed, await cartService.CountAsync(customerId, cancellationToken)));
    }

    // ---- Helpers ----

    /// <summary>Reason, note and shipment details for a transition, or the field errors to return.</summary>
    private static (string? Reason, string? Note, string? Carrier, string? Tracking, Dictionary<string, string[]> Errors) ValidateDetails(
        OrderActorType role, StoreOrderStatus target, ChangeStoreOrderStatusCommand command)
    {
        var errors = new Dictionary<string, string[]>();
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        if (note?.Length > OrderLimits.MaxNoteLength) errors["note"] = [$"The note can have at most {OrderLimits.MaxNoteLength} characters."];

        string? reason = null;
        if (target == StoreOrderStatus.Cancelled)
        {
            reason = command.Reason?.Trim();
            switch (role)
            {
                case OrderActorType.Customer:
                    if (reason is null || !CustomerCancelReasons.All.Contains(reason)) errors["reason"] = ["Choose why you are cancelling."];
                    else if (reason == CustomerCancelReasons.Other && note is null) errors["note"] = ["Tell the shop why you are cancelling."];
                    break;
                case OrderActorType.Vendor:
                    if (reason is null || !VendorCancelReasons.All.Contains(reason)) errors["reason"] = ["Choose why the shop cancels."];
                    else if (reason == VendorCancelReasons.Other && note is null) errors["note"] = ["Describe why the shop cancels."];
                    break;
                default:
                    // An administrator always explains; the customer and the shop both see the note.
                    reason = SystemCancelReasons.Admin;
                    if (note is null) errors["note"] = ["Describe why the order is cancelled."];
                    break;
            }
        }
        else
        {
            note = null;
        }

        string? carrier = null, tracking = null;
        if (target == StoreOrderStatus.Shipped)
            (carrier, tracking) = ValidateShipment(command, errors);
        return (reason, note, carrier, tracking, errors);
    }

    private static (string? Carrier, string? Tracking) ValidateShipment(ChangeStoreOrderStatusCommand command, Dictionary<string, string[]> errors)
    {
        var carrier = command.Carrier?.Trim();
        var tracking = command.TrackingNumber?.Trim();
        if (string.IsNullOrEmpty(carrier)) errors["carrier"] = ["Choose the carrier."];
        else if (carrier.Length > OrderLimits.MaxCarrierLength) errors["carrier"] = [$"The carrier can have at most {OrderLimits.MaxCarrierLength} characters."];
        if (string.IsNullOrEmpty(tracking)) errors["trackingNumber"] = ["Enter the tracking number."];
        else if (tracking.Length > OrderLimits.MaxTrackingNumberLength) errors["trackingNumber"] = [$"The tracking number can have at most {OrderLimits.MaxTrackingNumberLength} characters."];
        return (carrier, tracking);
    }

    private async Task<OrderResult<CustomerOrder>> UpdateShipmentAsync(
        StoreOrder storeOrder, ChangeStoreOrderStatusCommand command, OrderCaller caller, OrderActorType role, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var (carrier, tracking) = ValidateShipment(command, errors);
        if (errors.Count > 0) return OrderResult.Failure<CustomerOrder>(errors);

        if (!await orderStore.UpdateShipmentAsync(storeOrder.Id, carrier!, tracking!, caller.CustomerId, clock.UtcNow, cancellationToken))
            return OrderResult.Error<CustomerOrder>(OrderErrors.ConcurrentUpdate);

        await auditLog.WriteAsync("order.shipment_updated", caller.CustomerId, entityType: "StoreOrder", entityId: storeOrder.Id,
            details: new { subOrderNumber = storeOrder.SubOrderNumber }, cancellationToken: cancellationToken);
        var order = (await orderStore.GetAsync(storeOrder.OrderId, cancellationToken))!;
        return OrderResult.Success(role == OrderActorType.Vendor ? OnlyShop(order, storeOrder.VendorId) : order);
    }

    private async Task NotifyAsync(
        StoreOrder storeOrder, int customerId, OrderActorType role, StoreOrderStatus target, string? reason, string? note,
        string? carrier, string? tracking, CancellationToken cancellationToken)
    {
        if (role == OrderActorType.Customer)
        {
            if (target == StoreOrderStatus.Cancelled) await notifier.CancelledByCustomerAsync(storeOrder, reason, note, cancellationToken);
            return;
        }

        // The shop and administrators tell the customer what happened (vendor-orders-prd.md, FR-12).
        if (target is not (StoreOrderStatus.Confirmed or StoreOrderStatus.Shipped or StoreOrderStatus.Cancelled)) return;
        var customer = await identityStore.FindByIdAsync(customerId, cancellationToken);
        if (customer is null) return;
        await notifier.StatusChangedForCustomerAsync(storeOrder, target, customer.Email, reason, note, carrier, tracking, cancellationToken);
    }

    private static string AuditEvent(OrderActorType role, StoreOrderStatus target) => (role, target) switch
    {
        (OrderActorType.Customer, StoreOrderStatus.Cancelled) => "order.cancelled_by_customer",
        (OrderActorType.Customer, _) => "order.received",
        (OrderActorType.Admin, _) => "order.cancelled_by_admin",
        (_, StoreOrderStatus.Confirmed) => "order.confirmed",
        (_, StoreOrderStatus.Shipped) => "order.shipped",
        (_, StoreOrderStatus.Delivered) => "order.delivered",
        _ => "order.cancelled_by_vendor"
    };

    /// <summary>The order trimmed to one shop's part; the totals of the shop order are what the shop sees.</summary>
    private static CustomerOrder OnlyShop(CustomerOrder order, int vendorId)
    {
        order.StoreOrders = order.StoreOrders.Where(s => s.VendorId == vendorId).ToList();
        order.ItemsTotal = order.StoreOrders.Sum(s => s.ItemsTotal);
        order.ShippingTotal = order.StoreOrders.Sum(s => s.ShippingFee);
        order.Total = order.StoreOrders.Sum(s => s.Total);
        return order;
    }

    private static string? TrimSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var trimmed = search.Trim();
        return trimmed.Length > OrderLimits.MaxSearchLength ? trimmed[..OrderLimits.MaxSearchLength] : trimmed;
    }
}
