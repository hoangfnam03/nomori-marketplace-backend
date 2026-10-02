using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Catalog;

/// <summary>Special price and tier prices of the products of one shop. Products of other shops do not exist for the caller.</summary>
public sealed class ProductPricingService(
    IProductStore productStore,
    IVendorStore vendorStore,
    IPrimaryCurrencyProvider primaryCurrency,
    IAuditLogService auditLog,
    IClock clock) : IProductPricingService
{
    public async Task<CatalogResult<ProductPricing>> GetForVendorAsync(int vendorId, int productId, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null || product.VendorId != vendorId) return CatalogResult.Error<ProductPricing>(CatalogErrors.NotFound);
        return CatalogResult.Success(await ReadAsync(product, cancellationToken));
    }

    public async Task<CatalogResult<ProductPricing>> SetForVendorAsync(
        int vendorId, int productId, SavePricingCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null || product.VendorId != vendorId) return CatalogResult.Error<ProductPricing>(CatalogErrors.NotFound);

        var vendor = await vendorStore.GetAsync(vendorId, cancellationToken);
        if (vendor is null) return CatalogResult.Error<ProductPricing>(CatalogErrors.NotFound);
        if (!vendor.Active) return CatalogResult.Error<ProductPricing>(CatalogErrors.Forbidden);

        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>();
        var scale = $"Amounts in {currency.Code} can have at most {currency.DecimalPlaces} decimal place(s).";

        // ---- Special price and its window ----
        var start = AsUtc(command.SpecialPriceStartUtc);
        var end = AsUtc(command.SpecialPriceEndUtc);
        if (command.SpecialPrice is null)
        {
            if (start is not null || end is not null) errors["specialPrice"] = ["Set a special price, or clear the dates."];
        }
        else
        {
            var special = command.SpecialPrice.Value;
            if (special <= 0) errors["specialPrice"] = ["The special price must be greater than 0."];
            else if (special >= product.Price) errors["specialPrice"] = ["The special price must be lower than the regular price."];
            else if (!CurrencyRules.HasValidScale(special, currency.DecimalPlaces)) errors["specialPrice"] = [scale];
            if (start is { } s && end is { } e && e <= s) errors["specialPriceEndUtc"] = ["The end of the special price must be after its start."];
        }

        // ---- Tier prices ----
        var tiers = (command.TierPrices ?? []).OrderBy(t => t.Quantity).ToList();
        var tierError = ValidateTiers(tiers, product.Price, currency, scale);
        if (tierError is not null) errors["tierPrices"] = [tierError];

        if (errors.Count > 0) return CatalogResult.Failure<ProductPricing>(errors);

        product.SpecialPrice = command.SpecialPrice;
        product.SpecialPriceStartUtc = command.SpecialPrice is null ? null : start;
        product.SpecialPriceEndUtc = command.SpecialPrice is null ? null : end;
        product.UpdatedOnUtc = clock.UtcNow;
        await productStore.UpdatePricingAsync(product, cancellationToken);
        await productStore.SetTierPricesAsync(productId, tiers, cancellationToken);

        // Amounts and counts only: no free text exists here, and the numbers are what an audit needs.
        await auditLog.WriteAsync("product.pricing_changed", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId, specialPrice = command.SpecialPrice, tiers = tiers.Count }, cancellationToken: cancellationToken);
        return CatalogResult.Success(await ReadAsync(product, cancellationToken));
    }

    private static string? ValidateTiers(List<TierPrice> tiers, decimal regularPrice, Currency currency, string scaleMessage)
    {
        if (tiers.Count > PricingLimits.MaxTiers) return $"A product can have at most {PricingLimits.MaxTiers} tier prices.";

        var previousQuantity = 0;
        decimal? previousPrice = null;
        foreach (var tier in tiers)
        {
            if (tier.Quantity < PricingLimits.MinTierQuantity || tier.Quantity > PricingLimits.MaxQuantity)
                return $"A tier quantity must be between {PricingLimits.MinTierQuantity} and {PricingLimits.MaxQuantity}.";
            // The list is sorted, so a repeated quantity sits right after the first one.
            if (tier.Quantity == previousQuantity) return "The same quantity is listed more than once.";
            if (tier.Price <= 0) return "A tier price must be greater than 0.";
            if (!CurrencyRules.HasValidScale(tier.Price, currency.DecimalPlaces)) return scaleMessage;
            if (tier.Price >= regularPrice) return "A tier price must be lower than the regular price.";
            if (previousPrice is not null && tier.Price >= previousPrice) return "A larger quantity must have a lower price than the step before it.";
            previousQuantity = tier.Quantity;
            previousPrice = tier.Price;
        }
        return null;
    }

    private async Task<ProductPricing> ReadAsync(Product product, CancellationToken cancellationToken) =>
        new(product.SpecialPrice, product.SpecialPriceStartUtc, product.SpecialPriceEndUtc, await productStore.GetTierPricesAsync(product.Id, cancellationToken));

    private static DateTime? AsUtc(DateTime? value) => value is null ? null : value.Value.Kind switch
    {
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        _ => value.Value
    };
}
