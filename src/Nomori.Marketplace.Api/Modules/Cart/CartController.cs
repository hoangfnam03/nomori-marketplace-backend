using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Api.Modules.Cart;

/// <summary>
/// The cart of the signed-in customer. The customer always comes from the session, never from the request,
/// so there is no way to address someone else's cart. Amounts are in the primary currency.
/// </summary>
[ApiController]
[Route("api/v1/cart")]
[Authorize]
public sealed class CartController(ICartService cartService, ICurrentUser currentUser) : ControllerBase
{
    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> GetCart(CancellationToken cancellationToken) =>
        Ok(await cartService.GetAsync(CustomerId, cancellationToken));

    /// <summary>The number of units in the cart, for the header.</summary>
    [HttpGet("count")]
    public async Task<IActionResult> GetCount(CancellationToken cancellationToken) =>
        Ok(new { count = await cartService.CountAsync(CustomerId, cancellationToken) });

    [HttpPost("items")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(AddCartItemRequest request, CancellationToken cancellationToken)
    {
        var result = await cartService.AddAsync(CustomerId, new AddToCartCommand(request.ProductId, request.Quantity, request.ValueIds ?? []), cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }

    [HttpPut("items/{lineId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetQuantity(int lineId, SetCartQuantityRequest request, CancellationToken cancellationToken)
    {
        var result = await cartService.SetQuantityAsync(CustomerId, lineId, request.Quantity, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }

    [HttpDelete("items/{lineId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveItem(int lineId, CancellationToken cancellationToken)
    {
        var result = await cartService.RemoveAsync(CustomerId, lineId, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : this.ToFailure(result);
    }

    [HttpDelete]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken) =>
        Ok(await cartService.ClearAsync(CustomerId, cancellationToken));

    /// <summary>Saves the current unit prices as the ones the customer has seen, clearing the price notices.</summary>
    [HttpPost("accept-prices")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptPrices(CancellationToken cancellationToken) =>
        Ok(await cartService.AcceptPricesAsync(CustomerId, cancellationToken));
}

public sealed record AddCartItemRequest(int ProductId, int Quantity = 1, int[]? ValueIds = null);

public sealed record SetCartQuantityRequest(int Quantity);
