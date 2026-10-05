using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Shipping;

public static class ShippingLimits
{
    public const int MaxRatesPerShop = 50;
    public const int MaxNameLength = 100;
    public const int MaxDays = 365;
    public const decimal MaxAmount = 1_000_000m;
}

/// <summary>Business-rule codes of shipping (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class ShippingErrors
{
    public const string RateLimit = "shipping.rate_limit";
}

/// <summary>A flat shipping fee of one shop for one destination (a country, or one state of it).</summary>
public sealed class ShippingRate
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2, upper case.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>When set the rate covers only this state; otherwise the whole country.</summary>
    public int? StateProvinceId { get; set; }

    public decimal Fee { get; set; }

    /// <summary>When the shop subtotal in the cart reaches this amount the fee is 0.</summary>
    public decimal? FreeOverSubtotal { get; set; }

    public int? MinDays { get; set; }
    public int? MaxDays { get; set; }
    public bool Published { get; set; } = true;
    public int DisplayOrder { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public sealed record SaveShippingRateCommand(
    string? Name, string? CountryCode, int? StateProvinceId, decimal Fee, decimal? FreeOverSubtotal,
    int? MinDays, int? MaxDays, bool Published, int DisplayOrder);

/// <summary>What the customer names for a quote: one of their saved addresses, or a country (and state) for an estimate.</summary>
/// <param name="CartItemIds">Quote only these cart lines (a partial checkout); null or empty quotes the whole cart.</param>
public sealed record ShippingQuoteRequest(int? AddressId, string? CountryCode, int? StateProvinceId, IReadOnlyCollection<int>? CartItemIds = null);

public sealed record ShippingOption(int RateId, string Name, decimal Fee, bool IsFree, int? MinDays, int? MaxDays);

public sealed record ShippingShopQuote(
    int VendorId, string? VendorName, decimal Subtotal, IReadOnlyList<ShippingOption> Options, bool CanShip);

public sealed record ShippingQuote(
    string CurrencyCode, string CountryCode, int? StateProvinceId, IReadOnlyList<ShippingShopQuote> Shops,
    bool CanShipAll, decimal? ShippingTotal);

public static class ShippingRules
{
    /// <summary>A published rate covers a destination when the country is the same and the rate has no state or the same state.</summary>
    public static bool Matches(ShippingRate rate, string countryCode, int? stateProvinceId) =>
        rate.Published
        && string.Equals(rate.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)
        && (rate.StateProvinceId is null || rate.StateProvinceId == stateProvinceId);

    /// <summary>The fee for a shop subtotal; the threshold itself already ships free.</summary>
    public static decimal FeeFor(ShippingRate rate, decimal subtotal) =>
        rate.FreeOverSubtotal is { } threshold && subtotal >= threshold ? 0m : rate.Fee;

    /// <summary>The options of a shop for a destination, cheapest first, then by name.</summary>
    public static IReadOnlyList<ShippingOption> Options(
        IEnumerable<ShippingRate> rates, string countryCode, int? stateProvinceId, decimal subtotal) =>
        rates.Where(r => Matches(r, countryCode, stateProvinceId))
            .Select(r =>
            {
                var fee = FeeFor(r, subtotal);
                return new ShippingOption(r.Id, r.Name, fee, fee == 0m, r.MinDays, r.MaxDays);
            })
            .OrderBy(o => o.Fee).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.RateId)
            .ToList();

    /// <summary>The sum of the cheapest option of every shop; null when there is no shop or one of them cannot ship.</summary>
    public static decimal? ShippingTotal(IReadOnlyList<ShippingShopQuote> shops) =>
        shops.Count == 0 || shops.Any(s => !s.CanShip) ? null : shops.Sum(s => s.Options[0].Fee);
}

public interface IShippingStore
{
    Task<IReadOnlyList<ShippingRate>> GetRatesAsync(int vendorId, CancellationToken cancellationToken);
    Task<ShippingRate?> GetRateAsync(int vendorId, int id, CancellationToken cancellationToken);

    /// <summary>Published rates of the shops for one country (any state).</summary>
    Task<IReadOnlyList<ShippingRate>> GetPublishedRatesAsync(IReadOnlyCollection<int> vendorIds, string countryCode, CancellationToken cancellationToken);

    Task<int> CountRatesAsync(int vendorId, CancellationToken cancellationToken);
    Task<int> InsertAsync(ShippingRate rate, CancellationToken cancellationToken);
    Task UpdateAsync(ShippingRate rate, CancellationToken cancellationToken);

    /// <summary>True when a rate of this shop was removed.</summary>
    Task<bool> DeleteAsync(int vendorId, int id, CancellationToken cancellationToken);
}

public interface IShippingService
{
    // ---- Shop members (the caller has already been checked against the shop) ----

    Task<IReadOnlyList<ShippingRate>> GetRatesAsync(int vendorId, CancellationToken cancellationToken);
    Task<CatalogResult<ShippingRate>> CreateRateAsync(int vendorId, SaveShippingRateCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<ShippingRate>> UpdateRateAsync(int vendorId, int id, SaveShippingRateCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteRateAsync(int vendorId, int id, int actorCustomerId, CancellationToken cancellationToken);

    // ---- Customers ----

    /// <summary>Shipping options for every shop in the customer's cart, for a destination.</summary>
    Task<CatalogResult<ShippingQuote>> QuoteAsync(int customerId, ShippingQuoteRequest request, CancellationToken cancellationToken);
}
