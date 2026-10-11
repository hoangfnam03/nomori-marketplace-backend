using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Shipping;

public sealed record CarrierResponse(int Id, string Name, string? TrackingUrlTemplate, bool Active, int DisplayOrder)
{
    public static CarrierResponse From(Carrier c) => new(c.Id, c.Name, c.TrackingUrlTemplate, c.Active, c.DisplayOrder);
}

/// <summary>The pick list of a shop only needs names; the address template stays with the administrator.</summary>
public sealed record CarrierNameResponse(int Id, string Name);

public sealed record SaveCarrierRequest(string? Name, string? TrackingUrlTemplate, bool Active = true, int DisplayOrder = 0)
{
    public SaveCarrierCommand ToCommand() => new(Name, TrackingUrlTemplate, Active, DisplayOrder);
}

public sealed record TrackingEventResponse(long Id, string Status, string? Location, string? Note, DateTime OccurredOnUtc)
{
    public static TrackingEventResponse From(TrackingEvent e) => new(e.Id, e.Status, e.Location, e.Note, e.OccurredOnUtc);
}

public sealed record TrackingResponse(
    int ShopOrderId, string? Carrier, string? TrackingNumber, string? TrackingUrl, string OrderStatus, IReadOnlyList<TrackingEventResponse> Events)
{
    public static TrackingResponse From(TrackingView v) =>
        new(v.ShopOrderId, v.Carrier, v.TrackingNumber, v.TrackingUrl, v.OrderStatus, v.Events.Select(TrackingEventResponse.From).ToList());
}

public sealed record AddTrackingEventRequest(string? Status, string? Location, string? Note, DateTime? OccurredOnUtc);

/// <summary>The carriers a shop can pick when it ships.</summary>
[ApiController]
[Route("api/v1/shipping/carriers")]
[Authorize]
public sealed class CarriersController(ITrackingService tracking) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok((await tracking.GetCarriersAsync(true, cancellationToken)).Select(c => new CarrierNameResponse(c.Id, c.Name)));
}

/// <summary>Carriers for administrators who may manage orders.</summary>
[ApiController]
[Route("api/v1/admin/shipping/carriers")]
[Authorize]
[HasPermission(PermissionCodes.OrdersManage)]
public sealed class AdminCarriersController(ITrackingService tracking, ICurrentUser currentUser) : ControllerBase
{
    private int AdminId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok((await tracking.GetCarriersAsync(false, cancellationToken)).Select(CarrierResponse.From));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SaveCarrierRequest request, CancellationToken cancellationToken)
    {
        var result = await tracking.CreateCarrierAsync(request.ToCommand(), AdminId, cancellationToken);
        return result.Succeeded ? StatusCode(StatusCodes.Status201Created, CarrierResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, SaveCarrierRequest request, CancellationToken cancellationToken)
    {
        var result = await tracking.UpdateCarrierAsync(id, request.ToCommand(), AdminId, cancellationToken);
        return result.Succeeded ? Ok(CarrierResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await tracking.DeleteCarrierAsync(id, AdminId, cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }
}

/// <summary>
/// The journey of one parcel. One route for everyone who may look: the customer who bought it, a member of the shop, an administrator.
/// Anyone else gets 404.
/// </summary>
[ApiController]
[Route("api/v1/shop-orders/{shopOrderId:int}/tracking")]
[Authorize]
public sealed class ShopOrderTrackingController(
    ITrackingService tracking, ICurrentUser currentUser, IPermissionService permissions, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int shopOrderId, CancellationToken cancellationToken)
    {
        if (!int.TryParse(currentUser.Subject, out var customerId)) return Unauthorized();
        var vendor = await accessContext.GetCallerAsync(cancellationToken);
        var isAdmin = await permissions.HasPermissionAsync(customerId, PermissionCodes.OrdersManage, cancellationToken);
        var result = await tracking.GetTrackingAsync(new TrackingCaller(isAdmin, customerId, vendor.MemberVendorId), shopOrderId, cancellationToken);
        return result.Succeeded ? Ok(TrackingResponse.From(result.Value!)) : this.ToFailure(result);
    }
}

/// <summary>A shop tells where the parcel is. Only members of the shop; another shop's order is 404.</summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/orders/{id:int}/tracking-events")]
[Authorize]
public sealed class VendorTrackingController(ITrackingService tracking, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(int vendorId, int id, AddTrackingEventRequest request, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        if (!caller.IsAuthenticated || !caller.IsMemberOf(vendorId)) return NotFound();
        var result = await tracking.AddEventAsync(
            vendorId, id, new AddTrackingEventCommand(request.Status, request.Location, request.Note, request.OccurredOnUtc), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? StatusCode(StatusCodes.Status201Created, TrackingResponse.From(result.Value!)) : this.ToFailure(result);
    }
}
