using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Orders;

/// <summary>
/// The orders of one shop, for members of that shop (vendor-orders-prd.md, epic B). Anyone else gets 404. A shop sees only its own
/// part of an order and never the customer's email. Status changes go through <c>PUT /api/v1/store-orders/{id}/status</c>.
/// A shop switched off by an administrator can still process the orders it already has (FR-13).
/// </summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/orders")]
[Authorize]
public sealed class VendorOrdersController(IOrderService orderService, IVendorAccessContext accessContext, IVendorMemberStore memberStore) : ControllerBase
{
    /// <param name="from">First day of the order date range (yyyy-MM-dd, UTC), inclusive.</param>
    /// <param name="to">Last day of the order date range (yyyy-MM-dd, UTC), inclusive.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        int vendorId,
        [FromQuery] string? status = null, [FromQuery] string? search = null, [FromQuery] string? from = null, [FromQuery] string? to = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();

        var errors = new Dictionary<string, string[]>();
        var fromDay = ParseDay(from, "from", errors);
        var toDay = ParseDay(to, "to", errors);
        if (errors.Count > 0) return BadRequest(new ValidationProblemDetails(errors));

        Response.Headers.CacheControl = "private, no-store";
        var result = await orderService.ListForVendorAsync(
            new VendorOrderQuery(vendorId, status ?? VendorOrderTabs.All, search, fromDay, toDay?.AddDays(1), page, pageSize), cancellationToken);
        return Ok(StoreOrderPageResponse.From(result, OrderViewer.Vendor));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();

        Response.Headers.CacheControl = "private, no-store";
        var result = await orderService.GetForVendorAsync(vendorId, id, cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);
        return Ok(OrderResponse.From(result.Value!, OrderViewer.Vendor, await OrderActorNames.ForShopAsync(memberStore, vendorId, cancellationToken)));
    }

    /// <summary>Confirms several pending orders at once (US-B3). Orders that are no longer pending are reported as skipped.</summary>
    [HttpPost("confirm")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmMany(int vendorId, BulkConfirmRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();

        var result = await orderService.ConfirmManyAsync(vendorId, request.StoreOrderIds ?? [], caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }

    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsMemberOf(vendorId) ? caller : null;
    }

    private static DateTime? ParseDay(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
            return DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
        errors[field] = ["Use the date format yyyy-MM-dd."];
        return null;
    }
}
