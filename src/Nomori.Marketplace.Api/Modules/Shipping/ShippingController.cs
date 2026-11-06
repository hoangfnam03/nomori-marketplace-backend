using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Api.Modules.Shipping;

/// <summary>What shipping the signed-in customer's cart costs to one of their addresses (GHN, per shop).</summary>
[ApiController]
[Route("api/v1/shipping")]
[Authorize]
public sealed class ShippingController(IShippingService shippingService, ICurrentUser currentUser) : ControllerBase
{
    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpPost("quote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Quote(ShippingQuoteBody request, CancellationToken cancellationToken)
    {
        var result = await shippingService.QuoteAsync(CustomerId, new ShippingQuoteRequest(request.AddressId), cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }
}

public sealed record ShippingQuoteBody(int AddressId);
