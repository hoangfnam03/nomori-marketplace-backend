using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Vendors;
using OrderPaymentStatus = Nomori.Marketplace.Core.Orders.PaymentStatus;

namespace Nomori.Marketplace.Api.Modules.Orders;

internal static class OrderResultExtensions
{
    /// <summary>Maps a failed result to 400 (field errors), 404 (not found) or 409 (business rule; the code is in <c>detail</c>).</summary>
    public static IActionResult ToFailure<T>(this ControllerBase controller, OrderResult<T> result)
    {
        if (result.Errors.Count > 0)
            return controller.BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));

        return result.ErrorCode == OrderErrors.NotFound
            ? controller.NotFound()
            : controller.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Order operation failed", Detail = result.ErrorCode });
    }
}

/// <summary>Who a response is for: the actions it offers differ.</summary>
public enum OrderViewer
{
    Customer = 0,
    Vendor = 1
}

internal static class OrderNames
{
    public static string Of<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static TEnum? Parse<TEnum>(string? value) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}

// ---- Requests ----

public sealed record CheckoutPreviewRequest(int[]? CartItemIds);

public sealed record PlaceOrderRequest(
    int[]? CartItemIds, int AddressId, string? PaymentMethod, Dictionary<int, string?>? Notes, decimal ExpectedTotal);

public sealed record ChangeStoreOrderStatusRequest(string? Status, string? Reason, string? Note, string? Carrier = null, string? TrackingNumber = null);

public sealed record BulkConfirmRequest(int[]? StoreOrderIds);

// ---- Responses ----

public sealed record CheckoutPreviewResponse(
    string CurrencyCode, IReadOnlyList<CheckoutShopGroupResponse> Groups, decimal ItemsTotal, decimal ShippingTotal, decimal Total,
    bool CanPlace, IReadOnlyList<int> MissingCartItemIds, IReadOnlyList<int> OwnShopCartItemIds)
{
    public static CheckoutPreviewResponse From(CheckoutPreview p) => new(
        p.CurrencyCode,
        p.Groups.Select(g => new CheckoutShopGroupResponse(g.VendorId, g.VendorName, g.Lines, g.ItemsTotal, g.ShippingFee, g.Total)).ToList(),
        p.ItemsTotal, p.ShippingTotal, p.Total, p.CanPlace, p.MissingCartItemIds, p.OwnShopCartItemIds);
}

public sealed record CheckoutShopGroupResponse(
    int VendorId, string? VendorName, IReadOnlyList<CartLineView> Lines, decimal ItemsTotal, decimal ShippingFee, decimal Total);

public sealed record OrderAddressResponse(
    string FirstName, string LastName, string? Company, string Address1, string? Address2, string City,
    string? StateProvince, string CountryCode, string? ZipPostalCode, string PhoneNumber);

public sealed record OrderItemResponse(
    int Id, int ProductId, string ProductName, string? VariantDescription, string? Sku, int PictureId,
    decimal UnitPrice, int Quantity, decimal LineTotal);

/// <summary>
/// A status change. Everyone sees the role of who made it. Only the shop sees which of its members it was
/// (<see cref="ActorName"/>); the customer never sees a name.
/// </summary>
public sealed record StoreOrderEventResponse(
    string? FromStatus, string ToStatus, string Actor, string? Reason, string? Note, DateTime CreatedOnUtc, string? ActorName = null);

/// <summary>Names of a shop's members, to show the shop who did what in an order's history.</summary>
internal static class OrderActorNames
{
    public static async Task<IReadOnlyDictionary<int, string>> ForShopAsync(IVendorMemberStore members, int vendorId, CancellationToken cancellationToken) =>
        (await members.ListAsync(vendorId, cancellationToken)).ToDictionary(
            m => m.CustomerId,
            m => string.Join(' ', new[] { m.FirstName, m.LastName }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } name ? name : m.Email);
}

public sealed record StoreOrderResponse(
    int Id, int OrderId, string? OrderNumber, string SubOrderNumber, int VendorId, string? VendorName,
    string Status, string PaymentStatus, decimal ItemsTotal, decimal ShippingFee, decimal Total,
    string? CustomerNote, string? Carrier, string? TrackingNumber, DateTime ConfirmByUtc, DateTime CreatedOnUtc,
    DateTime? DeliveredOnUtc, DateTime? CancelledOnUtc, string? CancelReason, string? CancelNote, string? CancelledBy,
    bool CanCancel, bool CanConfirmReceipt, bool CanReorder,
    bool CanConfirm, bool CanShip, bool CanMarkDelivered, bool CanEditShipment,
    string? RecipientName, string? RecipientPhone, int ItemCount,
    IReadOnlyList<OrderItemResponse> Items, IReadOnlyList<StoreOrderEventResponse> Events)
{
    public static StoreOrderResponse From(StoreOrder s, OrderViewer viewer = OrderViewer.Customer, IReadOnlyDictionary<int, string>? memberNames = null)
    {
        var customer = viewer == OrderViewer.Customer;
        return new(
        s.Id, s.OrderId, s.OrderNumber, s.SubOrderNumber, s.VendorId, s.VendorName,
        OrderNames.Of(s.Status), OrderNames.Of(s.PaymentStatus), s.ItemsTotal, s.ShippingFee, s.Total,
        s.CustomerNote, s.Carrier, s.TrackingNumber, s.ConfirmByUtc, s.CreatedOnUtc, s.DeliveredOnUtc, s.CancelledOnUtc,
        s.CancelReason, s.CancelNote, s.CancelledBy is { } by ? OrderNames.Of(by) : null,
        CanCancel: customer ? OrderRules.CustomerMay(s.Status, StoreOrderStatus.Cancelled) : OrderRules.VendorMay(s.Status, StoreOrderStatus.Cancelled),
        CanConfirmReceipt: customer && OrderRules.CustomerMay(s.Status, StoreOrderStatus.Delivered),
        CanReorder: customer && s.Status is StoreOrderStatus.Delivered or StoreOrderStatus.Completed or StoreOrderStatus.Cancelled,
        CanConfirm: !customer && OrderRules.VendorMay(s.Status, StoreOrderStatus.Confirmed),
        CanShip: !customer && OrderRules.VendorMay(s.Status, StoreOrderStatus.Shipped),
        CanMarkDelivered: !customer && OrderRules.VendorMay(s.Status, StoreOrderStatus.Delivered),
        CanEditShipment: !customer && s.Status == StoreOrderStatus.Shipped,
        s.RecipientName, s.RecipientPhone, s.Items.Sum(i => i.Quantity),
        s.Items.Select(i => new OrderItemResponse(i.Id, i.ProductId, i.ProductName, i.VariantDescription, i.Sku, i.PictureId, i.UnitPrice, i.Quantity, i.LineTotal)).ToList(),
        s.Events.Select(e => new StoreOrderEventResponse(
            e.FromStatus is { } from ? OrderNames.Of(from) : null, OrderNames.Of(e.ToStatus), OrderNames.Of(e.ActorType), e.Reason, e.Note, e.CreatedOnUtc,
            !customer && e.ActorType == OrderActorType.Vendor && e.ActorCustomerId is { } actorId && memberNames is not null
                ? memberNames.GetValueOrDefault(actorId) : null)).ToList());
    }
}

public sealed record OrderResponse(
    int Id, string OrderNumber, DateTime CreatedOnUtc, string CurrencyCode, decimal ItemsTotal, decimal ShippingTotal, decimal Total,
    string PaymentMethod, string PaymentStatus, string OverallStatus, OrderAddressResponse ShippingAddress,
    bool CanCancelAll, IReadOnlyList<StoreOrderResponse> StoreOrders)
{
    public static OrderResponse From(CustomerOrder o, OrderViewer viewer = OrderViewer.Customer, IReadOnlyDictionary<int, string>? memberNames = null)
    {
        var a = o.ShippingAddress;
        return new OrderResponse(
            o.Id, o.OrderNumber, o.CreatedOnUtc, o.CurrencyCode, o.ItemsTotal, o.ShippingTotal, o.Total,
            OrderNames.Of(o.PaymentMethod), OrderNames.Of(OverallPayment(o.StoreOrders)), OrderNames.Of(o.OverallStatus),
            new OrderAddressResponse(a.FirstName, a.LastName, a.Company, a.Address1, a.Address2, a.City, a.StateProvince, a.CountryCode, a.ZipPostalCode, a.PhoneNumber),
            CanCancelAll: viewer == OrderViewer.Customer && o.StoreOrders.Count > 0 && o.StoreOrders.All(s => s.Status == StoreOrderStatus.Pending),
            o.StoreOrders.Select(s => StoreOrderResponse.From(Numbered(s, o.OrderNumber), viewer, memberNames)).ToList());
    }

    /// <summary>Paid once every shop order still going has been paid; voided when every one was cancelled before payment.</summary>
    private static OrderPaymentStatus OverallPayment(IReadOnlyList<StoreOrder> storeOrders)
    {
        var live = storeOrders.Where(s => s.PaymentStatus != OrderPaymentStatus.Voided).ToList();
        if (live.Count == 0) return OrderPaymentStatus.Voided;
        return live.All(s => s.PaymentStatus is OrderPaymentStatus.Paid or OrderPaymentStatus.Refunded) ? OrderPaymentStatus.Paid : OrderPaymentStatus.Pending;
    }

    private static StoreOrder Numbered(StoreOrder s, string orderNumber)
    {
        s.OrderNumber ??= orderNumber;
        return s;
    }
}

public sealed record StoreOrderPageResponse(
    IReadOnlyList<StoreOrderResponse> Items, int TotalCount, int Page, int PageSize, int TotalPages, IReadOnlyDictionary<string, int> TabCounts)
{
    public static StoreOrderPageResponse From(StoreOrderPage page, OrderViewer viewer = OrderViewer.Customer) => new(
        page.Items.Select(s => StoreOrderResponse.From(s, viewer)).ToList(), page.TotalCount, page.Page, page.PageSize,
        (int)Math.Ceiling(page.TotalCount / (double)page.PageSize), page.TabCounts);
}
