using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Services.Shipping;

public sealed class ShippingService(
    ICartService cartService,
    ICustomerAccountDataService addressService,
    IGhnShippingService ghn) : IShippingService
{
    public async Task<CatalogResult<ShippingQuote>> QuoteAsync(int customerId, ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        // Only the customer's own addresses are searched, so another customer's address is simply not found.
        var address = (await addressService.GetAddressesAsync(customerId, cancellationToken)).FirstOrDefault(a => a.Id == request.AddressId);
        if (address is null) return CatalogResult.Error<ShippingQuote>(CatalogErrors.NotFound);

        // GHN prices and delivers by its own province and ward (two-level units); an address without them has to be completed first.
        var place = address is { GhnProvinceId: { } provinceId, GhnWardId: { } wardId } ? new GhnPlace(provinceId, wardId) : null;
        if (ghn.IsConfigured && place is null) return CatalogResult.Error<ShippingQuote>(ShippingErrors.AreaMissing);

        // Free shipping over an amount counts only what is being bought now.
        var cart = CartRules.Narrow(await cartService.GetAsync(customerId, cancellationToken), request.CartItemIds);
        var shops = new List<ShippingShopQuote>();
        foreach (var group in cart.Groups)
        {
            var option = await ghn.QuoteAsync(
                group.VendorId, place, group.Lines.Select(l => new GhnLineRef(l.ProductId, l.Quantity)).ToList(), group.Subtotal, cancellationToken);
            IReadOnlyList<ShippingOption> options = option is null ? [] : [option];
            shops.Add(new ShippingShopQuote(group.VendorId, group.VendorName, group.Subtotal, options, options.Count > 0));
        }

        return CatalogResult.Success(new ShippingQuote(cart.CurrencyCode, shops, shops.Count > 0 && shops.All(s => s.CanShip), ShippingRules.ShippingTotal(shops)));
    }
}
