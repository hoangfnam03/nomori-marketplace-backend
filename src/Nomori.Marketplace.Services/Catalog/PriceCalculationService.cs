using System.Text.Json;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Catalog;

/// <summary>The one place that turns a product, a quantity and the chosen variant values into a price (see the pipeline in the F14-A document).</summary>
public sealed class PriceCalculationService(
    IProductStore productStore,
    IProductAttributeStore attributeStore,
    IPrimaryCurrencyProvider primaryCurrency,
    IClock clock) : IPriceCalculationService
{
    public async Task<CatalogResult<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken)
    {
        if (request.Quantity is < 1 or > PricingLimits.MaxQuantity)
            return CatalogResult.Failure<PriceQuote>("quantity", $"The quantity must be between 1 and {PricingLimits.MaxQuantity}.");

        var product = await productStore.GetAsync(request.ProductId, cancellationToken);
        if (product is null) return CatalogResult.Error<PriceQuote>(CatalogErrors.NotFound);

        var mappings = await attributeStore.GetMappingsAsync(product.Id, cancellationToken);
        var values = await attributeStore.GetValuesByProductAsync(product.Id, cancellationToken);
        var combinations = await attributeStore.GetCombinationsAsync(product.Id, cancellationToken);

        // The chosen values: all must belong to this product, at most one per option.
        var chosen = (request.ValueIds ?? []).Distinct().ToList();
        var byId = values.ToDictionary(v => v.Id);
        if (chosen.Any(id => !byId.ContainsKey(id)))
            return CatalogResult.Failure<PriceQuote>("valueIds", "A chosen value does not belong to this product.");
        var chosenByMapping = chosen.Select(id => byId[id]).GroupBy(v => v.ProductAttributeMappingId).ToDictionary(g => g.Key, g => g.ToList());
        if (chosenByMapping.Values.Any(list => list.Count > 1))
            return CatalogResult.Failure<PriceQuote>("valueIds", "Choose only one value for each option.");

        ProductAttributeCombination? combination = null;
        if (combinations.Count > 0)
        {
            if (mappings.Any(m => !chosenByMapping.ContainsKey(m.Id)))
                return CatalogResult.Failure<PriceQuote>("valueIds", "Choose one value for every option.");

            combination = combinations.FirstOrDefault(c => Matches(c, mappings, chosenByMapping));
            if (combination is null) return CatalogResult.Failure<PriceQuote>("valueIds", "This combination is not available.");
        }

        var now = clock.UtcNow;
        var tiers = await productStore.GetTierPricesAsync(product.Id, cancellationToken);
        var special = PriceRules.IsSpecialActive(product, now) ? product.SpecialPrice : null;
        var adjustments = chosen.Sum(id => byId[id].PriceAdjustment);

        var (unit, compare, rule) = PriceRules.Compose(
            product.Price, special, PriceRules.TierPriceFor(tiers, request.Quantity), adjustments, combination?.OverriddenPrice, product.OldPrice);

        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        var unitPrice = CurrencyRules.Round(unit, currency.DecimalPlaces);
        if (unitPrice <= 0) return CatalogResult.Failure<PriceQuote>("price", "The price for this choice is not valid.");

        return CatalogResult.Success(new PriceQuote(
            product.Id, combination?.Id, request.Quantity, currency.Code,
            CurrencyRules.Round(product.Price + (combination?.OverriddenPrice is null ? adjustments : 0), currency.DecimalPlaces),
            unitPrice,
            compare is { } c ? CurrencyRules.Round(c, currency.DecimalPlaces) : null,
            CurrencyRules.Round(unitPrice * request.Quantity, currency.DecimalPlaces),
            rule));
    }

    /// <summary>A combination matches when its key (mapping id to value id) names exactly the chosen value of every option.</summary>
    private static bool Matches(
        ProductAttributeCombination combination, IReadOnlyList<ProductAttributeMapping> mappings, Dictionary<int, List<ProductAttributeValue>> chosenByMapping)
    {
        try
        {
            var key = JsonSerializer.Deserialize<Dictionary<string, int>>(combination.AttributesJson);
            return key is not null && key.Count == mappings.Count
                && mappings.All(m => key.TryGetValue(m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var valueId)
                                     && chosenByMapping[m.Id][0].Id == valueId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
