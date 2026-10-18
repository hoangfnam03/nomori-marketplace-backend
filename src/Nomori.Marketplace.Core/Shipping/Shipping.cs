using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Shipping;

/// <summary>
/// Shipping is GHN's (Vietnam only): every shop sends from its own GHN pickup address and the fee comes from GHN. Shops no longer set rates
/// of their own (the F20-A rates were removed).
/// </summary>
public static class ShippingErrors
{
    /// <summary>The address has no GHN province, district and ward, so GHN cannot price or deliver to it.</summary>
    public const string AreaMissing = "shipping.area_missing";
}

/// <summary>What the customer ships to: one of their saved addresses.</summary>
/// <param name="CartItemIds">Quote only these cart lines (a partial checkout); null or empty quotes the whole cart.</param>
public sealed record ShippingQuoteRequest(int AddressId, IReadOnlyCollection<int>? CartItemIds = null);

/// <param name="Fee">What the customer pays.</param>
/// <param name="ShopCost">What the shop pays instead, when it offers free shipping (the GHN fee it takes on); 0 otherwise.</param>
/// <param name="EstimatedFromUtc">GHN's earliest expected delivery day, when it gave one.</param>
/// <param name="EstimatedToUtc">GHN's latest expected delivery day.</param>
public sealed record ShippingOption(
    int RateId, string Name, decimal Fee, bool IsFree, int? MinDays, int? MaxDays, decimal ShopCost = 0m,
    DateTime? EstimatedFromUtc = null, DateTime? EstimatedToUtc = null);

public sealed record ShippingShopQuote(
    int VendorId, string? VendorName, decimal Subtotal, IReadOnlyList<ShippingOption> Options, bool CanShip);

public sealed record ShippingQuote(string CurrencyCode, IReadOnlyList<ShippingShopQuote> Shops, bool CanShipAll, decimal? ShippingTotal);

public static class ShippingRules
{
    /// <summary>The sum of the cheapest option of every shop; null when there is no shop or one of them cannot ship.</summary>
    public static decimal? ShippingTotal(IReadOnlyList<ShippingShopQuote> shops) =>
        shops.Count == 0 || shops.Any(s => !s.CanShip) ? null : shops.Sum(s => s.Options[0].Fee);
}

public interface IShippingService
{
    /// <summary>The GHN option of every shop in the customer's cart for one of their addresses.</summary>
    Task<CatalogResult<ShippingQuote>> QuoteAsync(int customerId, ShippingQuoteRequest request, CancellationToken cancellationToken);
}
