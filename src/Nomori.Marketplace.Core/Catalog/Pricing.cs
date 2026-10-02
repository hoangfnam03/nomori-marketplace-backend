namespace Nomori.Marketplace.Core.Catalog;

public static class PricingLimits
{
    public const int MaxTiers = 20;
    public const int MinTierQuantity = 2;
    public const int MaxQuantity = 10_000;
}

/// <summary>From <see cref="Quantity"/> units on, each unit costs <see cref="Price"/> (primary currency).</summary>
public sealed record TierPrice(int Quantity, decimal Price);

/// <summary>The pricing a shop sets on a product, saved in one call.</summary>
public sealed record SavePricingCommand(
    decimal? SpecialPrice, DateTime? SpecialPriceStartUtc, DateTime? SpecialPriceEndUtc, IReadOnlyList<TierPrice> TierPrices);

public sealed record ProductPricing(
    decimal? SpecialPrice, DateTime? SpecialPriceStartUtc, DateTime? SpecialPriceEndUtc, IReadOnlyList<TierPrice> TierPrices);

/// <summary>A question to the price calculation: this product, this many units, these variant values.</summary>
public sealed record PriceRequest(int ProductId, int Quantity, IReadOnlyList<int> ValueIds);

public static class PriceRule
{
    public const string Base = "base";
    public const string Special = "special";
    public const string Tier = "tier";
    public const string VariantOverride = "variant_override";
}

/// <summary>The answer. Carries every field an order line needs to keep as its price snapshot (F18).</summary>
public sealed record PriceQuote(
    int ProductId,
    int? CombinationId,
    int Quantity,
    string CurrencyCode,
    decimal RegularPrice,
    decimal UnitPrice,
    decimal? ComparePrice,
    decimal LineTotal,
    string AppliedRule);

/// <summary>The price arithmetic, pure so every rule is testable without a database.</summary>
public static class PriceRules
{
    public static bool IsSpecialActive(Product product, DateTime nowUtc) =>
        product.SpecialPrice is > 0
        && (product.SpecialPriceStartUtc is null || product.SpecialPriceStartUtc <= nowUtc)
        && (product.SpecialPriceEndUtc is null || nowUtc < product.SpecialPriceEndUtc);

    /// <summary>The price of one unit before variants and quantity: the special price while its window is open, otherwise the regular price.</summary>
    public static decimal CurrentPrice(Product product, DateTime nowUtc) =>
        IsSpecialActive(product, nowUtc) ? product.SpecialPrice!.Value : product.Price;

    /// <summary>The tier price of the highest step the quantity reaches, or null below the first step.</summary>
    public static decimal? TierPriceFor(IReadOnlyList<TierPrice> tiers, int quantity) =>
        tiers.Where(t => t.Quantity <= quantity).OrderByDescending(t => t.Quantity).Select(t => (decimal?)t.Price).FirstOrDefault();

    /// <summary>
    /// Steps 3 to 5 of the pipeline: the lowest of regular, special and tier; then the combination's override price,
    /// or that base plus the price adjustments.
    /// </summary>
    public static (decimal Unit, decimal? Compare, string Rule) Compose(
        decimal regular, decimal? special, decimal? tier, decimal adjustments, decimal? overridePrice, decimal oldPrice)
    {
        // A price chosen for one variant is deliberate and wins over every general rule.
        if (overridePrice is { } fixedPrice) return (fixedPrice, null, PriceRule.VariantOverride);

        var best = regular;
        var rule = PriceRule.Base;
        if (special is { } s && s < best) { best = s; rule = PriceRule.Special; }
        if (tier is { } t && t < best) { best = t; rule = PriceRule.Tier; }

        var unit = best + adjustments;
        var regularTotal = regular + adjustments;
        decimal? compare = unit < regularTotal
            ? regularTotal
            : oldPrice > 0 && oldPrice + adjustments > unit ? oldPrice + adjustments : null;
        return (unit, compare, rule);
    }
}

public interface IPriceCalculationService
{
    /// <summary>
    /// The unit price and line total for a product, quantity and chosen variant values, in the primary currency.
    /// Does not check that the product is visible: callers (the storefront, the cart) do.
    /// </summary>
    Task<CatalogResult<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken);
}

public interface IProductPricingService
{
    Task<CatalogResult<ProductPricing>> GetForVendorAsync(int vendorId, int productId, CancellationToken cancellationToken);

    /// <summary>Replaces the special price, its window and all tier prices of a product of this shop.</summary>
    Task<CatalogResult<ProductPricing>> SetForVendorAsync(int vendorId, int productId, SavePricingCommand command, int actorCustomerId, CancellationToken cancellationToken);
}
