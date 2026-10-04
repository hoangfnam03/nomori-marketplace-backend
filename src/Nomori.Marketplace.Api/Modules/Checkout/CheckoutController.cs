using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Checkout;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Api.Modules.Checkout;

/// <summary>
/// Checkout of the signed-in customer's own cart. The customer always comes from the session. Nothing the browser adds up is read:
/// the server prices the cart, takes the shipping fees from the chosen options and computes the totals itself.
/// </summary>
[ApiController]
[Route("api/v1/checkout")]
[Authorize]
public sealed class CheckoutController(ICheckoutService checkoutService, ICurrentUser currentUser) : ControllerBase
{
    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    /// <summary>The cart, the options for the chosen address and the totals, with every problem. Changes nothing.</summary>
    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(CheckoutChoicesRequest request, CancellationToken cancellationToken) =>
        Ok(CheckoutPreviewResponse.From(await checkoutService.PreviewAsync(CustomerId, request.ToChoices(), cancellationToken)));

    [HttpPost("place")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Place(PlaceOrderBody request, CancellationToken cancellationToken)
    {
        var result = await checkoutService.PlaceAsync(CustomerId, request.ToRequest(), cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);

        var placed = PlacedOrderResponse.From(result.Value!);
        // A repeated key gets the order it made the first time, not a second one.
        return result.Value!.Replayed ? Ok(placed) : StatusCode(StatusCodes.Status201Created, placed);
    }
}

public sealed record ShippingChoiceBody(int VendorId, int RateId);

public sealed record CheckoutChoicesRequest(int? AddressId, ShippingChoiceBody[]? ShippingChoices, string? PaymentMethod)
{
    public CheckoutChoices ToChoices() => new(AddressId, ShippingChoices?.Select(c => new ShippingChoice(c.VendorId, c.RateId)).ToList(), PaymentMethod);
}

public sealed record PlaceOrderBody(
    int? AddressId, ShippingChoiceBody[]? ShippingChoices, string? PaymentMethod, string? IdempotencyKey, bool AcceptedTerms, string? Note)
{
    public PlaceOrderRequest ToRequest() => new(
        AddressId, ShippingChoices?.Select(c => new ShippingChoice(c.VendorId, c.RateId)).ToList(), PaymentMethod, IdempotencyKey, AcceptedTerms, Note);
}

public sealed record CheckoutShippingOptionResponse(int RateId, string Name, decimal Fee, bool IsFree, int? MinDays, int? MaxDays)
{
    public static CheckoutShippingOptionResponse From(ShippingOption o) => new(o.RateId, o.Name, o.Fee, o.IsFree, o.MinDays, o.MaxDays);
}

public sealed record CheckoutShopResponse(
    int VendorId, string? VendorName, decimal Subtotal, IReadOnlyList<CheckoutShippingOptionResponse> Options, int? ChosenRateId, decimal? ShippingFee);

public sealed record CheckoutPaymentMethodResponse(string SystemName, string DisplayName, bool IsOffline);

public sealed record CheckoutPreviewResponse(
    Core.Cart.CartView Cart, int? AddressId, IReadOnlyList<CheckoutShopResponse> Shops, IReadOnlyList<CheckoutPaymentMethodResponse> PaymentMethods,
    string? PaymentMethod, decimal Subtotal, decimal? ShippingTotal, decimal? Total, IReadOnlyList<string> Problems, bool CanPlace)
{
    public static CheckoutPreviewResponse From(CheckoutPreview p) => new(
        p.Cart, p.AddressId,
        p.Shops.Select(s => new CheckoutShopResponse(
            s.VendorId, s.VendorName, s.Subtotal, s.Options.Select(CheckoutShippingOptionResponse.From).ToList(), s.Chosen?.RateId, s.Chosen?.Fee)).ToList(),
        p.PaymentMethods.Select(m => new CheckoutPaymentMethodResponse(m.SystemName, m.DisplayName, m.Kind == PaymentProviderKind.Offline)).ToList(),
        p.PaymentMethod, p.Subtotal, p.ShippingTotal, p.Total, p.Problems, p.CanPlace);
}

public sealed record PlacedShopOrderResponse(int Id, string Number, int VendorId, string ShopName, decimal Total);

public sealed record PlacedOrderResponse(
    int OrderId, string Number, string CurrencyCode, decimal Subtotal, decimal ShippingTotal, decimal Total, string PaymentMethod,
    string? PaymentStatus, bool Replayed, IReadOnlyList<PlacedShopOrderResponse> ShopOrders)
{
    public static PlacedOrderResponse From(PlacedOrder placed)
    {
        var o = placed.Order;
        return new PlacedOrderResponse(
            o.Id, o.Number, o.CurrencyCode, o.Subtotal, o.ShippingTotal, o.Total, o.PaymentMethod,
            placed.Payment is null ? null : PaymentRules.ToWire(placed.Payment.Status), placed.Replayed,
            o.ShopOrders.Select(s => new PlacedShopOrderResponse(s.Id, s.Number, s.VendorId, s.ShopName, s.Total)).ToList());
    }
}
