using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Orders;

/// <summary>
/// The orders of the signed-in customer. The customer always comes from the session; someone else's order is 404.
/// Orders are never created here: checkout does that through the service.
/// </summary>
[ApiController]
[Route("api/v1/orders")]
[Authorize]
public sealed class CustomerOrdersController(IOrderService orderService, ICurrentUser currentUser) : ControllerBase
{
    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken cancellationToken = default)
    {
        var paged = await orderService.GetMyOrdersAsync(CustomerId, page, pageSize, cancellationToken);
        return Ok(OrderMapping.Page(paged, OrderSummaryResponse.From));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var result = await orderService.GetMyOrderAsync(CustomerId, id, cancellationToken);
        return result.Succeeded ? Ok(OrderDetailResponse.From(result.Value!, forAdmin: false)) : this.ToFailure(result);
    }

    [HttpPost("shop-orders/{shopOrderId:int}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int shopOrderId, ReasonRequest request, CancellationToken cancellationToken)
    {
        var result = await orderService.CancelAsCustomerAsync(CustomerId, shopOrderId, request.Reason, cancellationToken);
        return result.Succeeded ? Ok(ShopOrderDetailResponse.From(result.Value!, forAdmin: false)) : this.ToFailure(result);
    }

    [HttpPost("shop-orders/{shopOrderId:int}/confirm-receipt")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmReceipt(int shopOrderId, CancellationToken cancellationToken)
    {
        var result = await orderService.ConfirmReceiptAsync(CustomerId, shopOrderId, cancellationToken);
        return result.Succeeded ? Ok(ShopOrderDetailResponse.From(result.Value!, forAdmin: false)) : this.ToFailure(result);
    }
}

/// <summary>
/// The shop orders of one shop, for members of that shop. Anyone else gets 404, and so does a shop order of another shop.
/// The shop id comes from the route and is checked against the membership on every call.
/// </summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/orders")]
[Authorize]
public sealed class VendorOrdersController(IOrderService orderService, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        int vendorId, [FromQuery] string? status, [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        if (!OrderMapping.TryStatus(status, out var parsed)) return this.ToFailure(CatalogResult.Failure<object>("status", "Unknown status."));

        var paged = await orderService.GetShopOrdersAsync(vendorId, OrderMapping.Query(null, parsed, search, from, to, page, pageSize), cancellationToken);
        return Ok(OrderMapping.Page(paged, ShopOrderListItemResponse.From));
    }

    [HttpGet("counts")]
    public async Task<IActionResult> Counts(int vendorId, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var counts = await orderService.GetCountsAsync(vendorId, cancellationToken);
        return Ok(counts.ToDictionary(c => OrderRules.ToWire(c.Key), c => c.Value));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var result = await orderService.GetShopOrderAsync(vendorId, id, cancellationToken);
        return Respond(result);
    }

    [HttpPost("{id:int}/confirm")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        return Respond(await orderService.ConfirmAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken));
    }

    [HttpPost("{id:int}/ship")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ship(int vendorId, int id, ShipRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        return Respond(await orderService.ShipAsync(vendorId, id, new ShipCommand(request.Carrier, request.TrackingNumber), caller.CustomerId!.Value, cancellationToken));
    }

    [HttpPut("{id:int}/tracking")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTracking(int vendorId, int id, ShipRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        return Respond(await orderService.UpdateTrackingAsync(vendorId, id, new ShipCommand(request.Carrier, request.TrackingNumber), caller.CustomerId!.Value, cancellationToken));
    }

    [HttpPost("{id:int}/deliver")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deliver(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        return Respond(await orderService.DeliverAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken));
    }

    [HttpPost("{id:int}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int vendorId, int id, ReasonRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        return Respond(await orderService.CancelAsShopAsync(vendorId, id, request.Reason, caller.CustomerId!.Value, cancellationToken));
    }

    private IActionResult Respond(CatalogResult<ShopOrderDetail> result) =>
        result.Succeeded ? Ok(ShopOrderDetailResponse.From(result.Value!, forAdmin: false)) : this.ToFailure(result);

    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

[ApiController]
[Route("api/v1/admin/orders")]
[Authorize]
[HasPermission(PermissionCodes.OrdersManage)]
public sealed class AdminOrdersController(IOrderService orderService, ICurrentUser currentUser) : ControllerBase
{
    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? vendorId, [FromQuery] string? status, [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!OrderMapping.TryStatus(status, out var parsed)) return this.ToFailure(CatalogResult.Failure<object>("status", "Unknown status."));
        var query = OrderMapping.Query(vendorId, parsed, search, from, to, page, pageSize);
        return Ok(OrderMapping.Page(await orderService.GetOrdersAsync(query, cancellationToken), OrderSummaryResponse.From));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var result = await orderService.GetOrderAsync(id, cancellationToken);
        return result.Succeeded ? Ok(OrderDetailResponse.From(result.Value!, forAdmin: true)) : this.ToFailure(result);
    }

    [HttpPost("shop-orders/{shopOrderId:int}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int shopOrderId, ReasonRequest request, CancellationToken cancellationToken)
    {
        var result = await orderService.CancelAsAdminAsync(shopOrderId, request.Reason, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(ShopOrderDetailResponse.From(result.Value!, forAdmin: true)) : this.ToFailure(result);
    }
}

public sealed record ReasonRequest(string? Reason);

public sealed record ShipRequest(string? Carrier, string? TrackingNumber);

internal static class OrderMapping
{
    public static bool TryStatus(string? value, out ShopOrderStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!OrderRules.TryParseWire(value, out var parsed)) return false;
        status = parsed;
        return true;
    }

    /// <summary>The "to" date is a day, so it includes that whole day.</summary>
    public static OrderListQuery Query(int? vendorId, ShopOrderStatus? status, string? search, DateTime? from, DateTime? to, int page, int pageSize) =>
        new(null, vendorId, status, search, from is null ? null : DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Utc),
            to is null ? null : DateTime.SpecifyKind(to.Value.Date.AddDays(1), DateTimeKind.Utc), page, pageSize);

    public static CatalogPagedResponse<TResponse> Page<TItem, TResponse>(PagedResult<TItem> paged, Func<TItem, TResponse> map) =>
        new(paged.Items.Select(map).ToList(), paged.TotalCount, paged.Page, paged.PageSize,
            paged.PageSize == 0 ? 0 : (int)Math.Ceiling(paged.TotalCount / (double)paged.PageSize));

    /// <summary>Administrators are shown to customers and shops as "platform", without their identity.</summary>
    public static string Actor(OrderActor actor) => actor == OrderActor.Admin ? "platform" : OrderRules.ToWire(actor);
}

public sealed record ShopOrderSummaryResponse(int Id, string Number, int VendorId, string ShopName, string Status, decimal Total, int ItemCount)
{
    public static ShopOrderSummaryResponse From(ShopOrder s) =>
        new(s.Id, s.Number, s.VendorId, s.ShopName, OrderRules.ToWire(s.Status), s.Total, s.ItemCount);
}

public sealed record OrderSummaryResponse(
    int Id, string Number, string CurrencyCode, decimal Total, string Status, string PaymentMethod, DateTime CreatedOnUtc,
    IReadOnlyList<ShopOrderSummaryResponse> ShopOrders)
{
    public static OrderSummaryResponse From(Order o) => new(
        o.Id, o.Number, o.CurrencyCode, o.Total, OrderRules.ToWire(OrderRules.Overall(o.ShopOrders.Select(s => s.Status))), o.PaymentMethod, o.CreatedOnUtc,
        o.ShopOrders.Select(ShopOrderSummaryResponse.From).ToList());
}

/// <summary>One row of a shop's list. There is no customer id or email: a shop only needs to know where to send the parcel.</summary>
public sealed record ShopOrderListItemResponse(
    int Id, string Number, string OrderNumber, string Status, DateTime CreatedOnUtc, int ItemCount, decimal Total, decimal ShippingFee, decimal DiscountAmount,
    string CurrencyCode, string PaymentMethod, string RecipientName, string RecipientPhone)
{
    public static ShopOrderListItemResponse From(ShopOrder s) => new(
        s.Id, s.Number, s.Order!.Number, OrderRules.ToWire(s.Status), s.CreatedOnUtc, s.ItemCount, s.Total, s.ShippingFee, s.DiscountAmount,
        s.Order.CurrencyCode, s.Order.PaymentMethod, s.Order.RecipientName, s.Order.RecipientPhone);
}

public sealed record OrderLineResponse(
    int Id, int ProductId, int? CombinationId, string Name, string? VariantLabel, string? Sku, int PictureId, int Quantity, decimal UnitPrice, decimal LineTotal)
{
    public static OrderLineResponse From(OrderLine l) =>
        new(l.Id, l.ProductId, l.CombinationId, l.Name, l.VariantLabel, l.Sku, l.PictureId, l.Quantity, l.UnitPrice, l.LineTotal);
}

public sealed record OrderHistoryResponse(string? FromStatus, string To, string Actor, int? ActorCustomerId, string? Note, DateTime CreatedOnUtc)
{
    public static OrderHistoryResponse Of(OrderHistoryEntry h, bool forAdmin) => new(
        h.FromStatus is { } from ? OrderRules.ToWire(from) : null, OrderRules.ToWire(h.ToStatus),
        forAdmin ? OrderRules.ToWire(h.Actor) : OrderMapping.Actor(h.Actor), forAdmin ? h.ActorCustomerId : null, h.Note, h.CreatedOnUtc);
}

public sealed record RecipientResponse(
    string Name, string Phone, string Address1, string? Address2, string City, string? StateProvince, string? PostalCode, string CountryCode)
{
    public static RecipientResponse From(Order o) =>
        new(o.RecipientName, o.RecipientPhone, o.Address1, o.Address2, o.City, o.StateProvince, o.PostalCode, o.CountryCode);
}

public sealed record ShopOrderDetailResponse(
    int Id, string Number, int OrderId, string OrderNumber, int VendorId, string ShopName, string Status, string CurrencyCode, string PaymentMethod,
    decimal Subtotal, decimal ShippingFee, decimal DiscountAmount, string? DiscountFunding, decimal Total, string ShippingMethodName, string? Carrier, string? TrackingNumber, string? CancelReason,
    string? CustomerNote, RecipientResponse Recipient, DateTime CreatedOnUtc, DateTime UpdatedOnUtc,
    IReadOnlyList<OrderLineResponse> Lines, IReadOnlyList<OrderHistoryResponse> History)
{
    public static ShopOrderDetailResponse From(ShopOrderDetail detail, bool forAdmin) =>
        From(detail.ShopOrder, detail.ShopOrder.Order!, detail.History, forAdmin);

    public static ShopOrderDetailResponse From(ShopOrder s, Order o, IReadOnlyList<OrderHistoryEntry> history, bool forAdmin) => new(
        s.Id, s.Number, s.OrderId, o.Number, s.VendorId, s.ShopName, OrderRules.ToWire(s.Status), o.CurrencyCode, o.PaymentMethod,
        s.Subtotal, s.ShippingFee, s.DiscountAmount, s.DiscountFunding, s.Total, s.ShippingMethodName, s.Carrier, s.TrackingNumber, s.CancelReason, o.CustomerNote, RecipientResponse.From(o),
        s.CreatedOnUtc, s.UpdatedOnUtc, s.Lines.Select(OrderLineResponse.From).ToList(), history.Select(h => OrderHistoryResponse.Of(h, forAdmin)).ToList());
}

public sealed record OrderDetailResponse(
    int Id, string Number, int CustomerId, string CurrencyCode, decimal Subtotal, decimal ShippingTotal, decimal DiscountTotal, string? DiscountCode, decimal Total, string Status, string PaymentMethod,
    string? CustomerNote, RecipientResponse Recipient, DateTime CreatedOnUtc, IReadOnlyList<ShopOrderDetailResponse> ShopOrders)
{
    public static OrderDetailResponse From(OrderDetail detail, bool forAdmin)
    {
        var o = detail.Order;
        return new OrderDetailResponse(
            o.Id, o.Number, forAdmin ? o.CustomerId : 0, o.CurrencyCode, o.Subtotal, o.ShippingTotal, o.DiscountTotal, o.DiscountCode, o.Total,
            OrderRules.ToWire(OrderRules.Overall(o.ShopOrders.Select(s => s.Status))), o.PaymentMethod, o.CustomerNote, RecipientResponse.From(o), o.CreatedOnUtc,
            o.ShopOrders.Select(s => ShopOrderDetailResponse.From(s, o, detail.History.GetValueOrDefault(s.Id) ?? [], forAdmin)).ToList());
    }
}
