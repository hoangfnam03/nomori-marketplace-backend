using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Orders;

/// <summary>
/// Checkout and "My orders" (customer-orders-prd.md, section 11). The customer always comes from the session, so nobody can
/// address another customer's cart or orders; another customer's order is reported as not found.
/// </summary>
[ApiController]
[Authorize]
public sealed class OrdersController(
    ICheckoutService checkoutService, IOrderService orderService, ICurrentUser currentUser, IVendorAccessContext accessContext,
    IVendorMemberStore memberStore) : ControllerBase
{
    private const string IdempotencyHeader = "Idempotency-Key";

    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    /// <summary>What placing an order from these cart items would create now. Writes nothing.</summary>
    [HttpPost("api/v1/checkout/preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(CheckoutPreviewRequest request, CancellationToken cancellationToken)
    {
        var result = await checkoutService.PreviewAsync(CustomerId, request.CartItemIds ?? [], cancellationToken);
        return result.Succeeded ? Ok(CheckoutPreviewResponse.From(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>
    /// Places an order. 201 when this request created it; 200 with the same order when a request with the same
    /// <c>Idempotency-Key</c> already did. 409 <c>order.total_changed</c> means the customer must look at the new total first.
    /// </summary>
    [HttpPost("api/v1/orders")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Place(
        PlaceOrderRequest request, [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey, CancellationToken cancellationToken)
    {
        var result = await checkoutService.PlaceAsync(CustomerId, new PlaceOrderCommand(
            request.CartItemIds ?? [], request.AddressId, OrderNames.Parse<PaymentMethod>(request.PaymentMethod),
            request.Notes, request.ExpectedTotal, idempotencyKey), cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);

        var response = OrderResponse.From(result.Value!.Order);
        return result.Value.Created ? Created($"/api/v1/orders/{response.Id}", response) : Ok(response);
    }

    /// <summary>The customer's shop orders, newest first, with the count of every tab.</summary>
    [HttpGet("api/v1/orders")]
    public async Task<IActionResult> List(
        [FromQuery] string? status = null, [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        var result = await orderService.ListForCustomerAsync(
            new StoreOrderQuery(CustomerId, status ?? OrderTabs.All, search, page, pageSize), cancellationToken);
        return Ok(StoreOrderPageResponse.From(result));
    }

    [HttpGet("api/v1/orders/{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var result = await orderService.GetForCustomerAsync(CustomerId, id, cancellationToken);
        return result.Succeeded ? Ok(OrderResponse.From(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>
    /// Changes the status of a shop order, for its customer, a member of its shop or an administrator (vendor-orders-prd.md, section 5):
    /// the customer cancels a pending one or confirms receipt; the shop confirms, ships (with carrier and tracking number; sending
    /// "shipped" again corrects them), marks delivered or cancels before shipping; an administrator cancels with a note.
    /// Anyone else gets 404. Returns the order as the caller may see it.
    /// </summary>
    [HttpPut("api/v1/store-orders/{id:int}/status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStatus(int id, ChangeStoreOrderStatusRequest request, CancellationToken cancellationToken)
    {
        var vendorCaller = await accessContext.GetCallerAsync(cancellationToken);
        var caller = new OrderCaller(CustomerId, vendorCaller.IsAdmin, vendorCaller.MemberVendorId);
        var result = await orderService.ChangeStatusAsync(id,
            new ChangeStoreOrderStatusCommand(OrderNames.Parse<StoreOrderStatus>(request.Status), request.Reason, request.Note, request.Carrier, request.TrackingNumber),
            caller, cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);

        var order = result.Value!;
        var viewer = order.StoreOrders.Count == 1 && order.StoreOrders[0].VendorId == caller.MemberVendorId && order.CustomerId != CustomerId
            ? OrderViewer.Vendor : OrderViewer.Customer;
        var names = viewer == OrderViewer.Vendor ? await OrderActorNames.ForShopAsync(memberStore, caller.MemberVendorId!.Value, cancellationToken) : null;
        return Ok(OrderResponse.From(order, viewer, names));
    }

    /// <summary>Adds the items of a shop order back to the cart at today's prices; reports the ones that could not be added.</summary>
    [HttpPost("api/v1/store-orders/{id:int}/reorder")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder(int id, CancellationToken cancellationToken)
    {
        var result = await orderService.ReorderAsync(CustomerId, id, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }
}
