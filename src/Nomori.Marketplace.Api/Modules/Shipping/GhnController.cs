using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Api.Modules.Orders;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Shipping;

/// <summary>GHN's own provinces, districts and wards, for the address form. Signed-in users only, since every call spends the platform's GHN quota.</summary>
[ApiController]
[Route("api/v1/shipping/ghn")]
[Authorize]
public sealed class GhnController(IGhnShippingService ghn) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status() => Ok(new { enabled = ghn.IsConfigured });

    [HttpGet("provinces")]
    public async Task<IActionResult> Provinces(CancellationToken cancellationToken) => Answer(await ghn.GetProvincesAsync(cancellationToken));

    [HttpGet("districts")]
    public async Task<IActionResult> Districts([FromQuery] int provinceId, CancellationToken cancellationToken) =>
        provinceId > 0 ? Answer(await ghn.GetDistrictsAsync(provinceId, cancellationToken)) : BadRequest();

    [HttpGet("wards")]
    public async Task<IActionResult> Wards([FromQuery] int districtId, CancellationToken cancellationToken) =>
        districtId > 0 ? Answer(await ghn.GetWardsAsync(districtId, cancellationToken)) : BadRequest();

    /// <summary>
    /// GHN's order callback. Anonymous (GHN has no account here) but only with the shared secret in <c>X-Nomori-Webhook-Secret</c>; without a
    /// configured secret it does not exist. 200 for everything it understood or ignored, so GHN only retries real failures.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook([FromHeader(Name = "X-Nomori-Webhook-Secret")] string? secret, [FromBody] GhnWebhook body, CancellationToken cancellationToken)
    {
        if (!ghn.IsWebhookSecret(secret)) return NotFound();
        var outcome = await ghn.HandleWebhookAsync(body, cancellationToken);
        return Ok(new { outcome = outcome.ToString().ToLowerInvariant() });
    }

    private IActionResult Answer<T>(GhnResult<T> result) =>
        result.Succeeded
            ? Ok(result.Value)
            : StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails { Status = StatusCodes.Status502BadGateway, Title = "GHN", Detail = result.Error });
}

/// <summary>Where GHN picks up the shop's parcels. Saving one turns GHN on for the shop; deleting it turns GHN off.</summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/ghn/pickup")]
[Authorize]
public sealed class VendorGhnPickupController(IGhnShippingService ghn, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int vendorId, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        return Ok(new { enabled = ghn.IsConfigured, pickup = await ghn.GetPickupAsync(vendorId, cancellationToken) });
    }

    [HttpPut]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int vendorId, SaveGhnPickupCommand request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await ghn.SavePickupAsync(vendorId, request, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }

    [HttpDelete]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int vendorId, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        await ghn.DeletePickupAsync(vendorId, caller.CustomerId!.Value, cancellationToken);
        return NoContent();
    }

    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

/// <summary>A shop member hands a confirmed shop order to GHN, pulls its journey, and prints its label.</summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/orders/{id:int}/ghn")]
[Authorize]
public sealed class VendorGhnController(IGhnShippingService ghn, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpPost("shipment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateShipment(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        return Respond(await ghn.CreateShipmentAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken));
    }

    [HttpPost("sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        return Respond(await ghn.SyncAsync(vendorId, id, caller.CustomerId!.Value, cancellationToken));
    }

    [HttpGet("label")]
    public async Task<IActionResult> Label(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var result = await ghn.GetLabelUrlAsync(vendorId, id, cancellationToken);
        return result.Succeeded ? Ok(new { url = result.Value }) : this.ToFailure(result);
    }

    private IActionResult Respond(CatalogResult<ShopOrderDetail> result) =>
        result.Succeeded ? Ok(ShopOrderDetailResponse.From(result.Value!, forAdmin: false)) : this.ToFailure(result);

    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}
