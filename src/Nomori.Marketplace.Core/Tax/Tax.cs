using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Discounts;

namespace Nomori.Marketplace.Core.Tax;

public static class TaxLimits
{
    public const int MaxCategories = 20;
    public const int MaxRatesPerCategory = 500;
    public const int MaxNameLength = 100;
    public const decimal MaxPercentage = 100m;
    public const int PercentageDecimals = 3;
}

/// <summary>Business-rule codes of tax (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class TaxErrors
{
    public const string CategoryExists = "tax.category_exists";
    public const string CategoryLimit = "tax.category_limit";
    public const string CategoryInUse = "tax.category_in_use";
    public const string DefaultCategory = "tax.default_category";
    public const string RateExists = "tax.rate_exists";
    public const string RateLimit = "tax.rate_limit";
}

public sealed class TaxCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Products with no assignment are in this category. Exactly one category is the default.</summary>
    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

/// <summary>The tax of a category in a country, or in one state of it.</summary>
public sealed class TaxRate
{
    public int Id { get; set; }
    public int CategoryId { get; set; }

    /// <summary>ISO 3166-1 alpha-2, upper case.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>When set the rate covers only this state; otherwise the whole country.</summary>
    public int? StateProvinceId { get; set; }

    /// <summary>0 to 100.</summary>
    public decimal Percentage { get; set; }

    public bool Published { get; set; } = true;
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public sealed record SaveTaxCategoryCommand(string? Name, int DisplayOrder);

public sealed record SaveTaxRateCommand(string? CountryCode, int? StateProvinceId, decimal Percentage, bool Published);

/// <summary>A product's tax category, and whether it was assigned or is the default.</summary>
public sealed record ProductTaxView(int ProductId, string ProductName, int TaxCategoryId, string TaxCategoryName, bool Assigned);

/// <summary>What is sold: one line of a shop, before tax and before the discount.</summary>
public sealed record TaxableLine(int VendorId, int ProductId, decimal LineTotal);

public sealed record TaxedLine(int VendorId, int ProductId, decimal Rate, decimal Base, decimal Tax);

/// <summary>The tax of a cart for a destination: per line (in the order given), per shop, and in all.</summary>
public sealed record TaxCalculation(IReadOnlyList<TaxedLine> Lines, IReadOnlyDictionary<int, decimal> ShopTax, decimal Total)
{
    public static TaxCalculation None { get; } = new([], new Dictionary<int, decimal>(), 0m);
}

public static class TaxRules
{
    /// <summary>
    /// The percentage for a category at a destination: the rate of the state when there is one, else the rate of the country, else 0.
    /// Only published rates count.
    /// </summary>
    public static decimal FindRate(IEnumerable<TaxRate> rates, int categoryId, string countryCode, int? stateProvinceId)
    {
        var candidates = rates.Where(r => r.Published && r.CategoryId == categoryId
            && string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)).ToList();

        if (stateProvinceId is { } state && candidates.FirstOrDefault(r => r.StateProvinceId == state) is { } stateRate) return stateRate.Percentage;
        return candidates.FirstOrDefault(r => r.StateProvinceId is null)?.Percentage ?? 0m;
    }

    /// <summary>
    /// Tax per line. The discount of a shop is spread over its lines in proportion to their totals (the leftover goes to the largest line),
    /// so the base is what the customer pays for the line; tax is never charged on a discount.
    /// </summary>
    public static TaxCalculation Calculate(
        IReadOnlyList<TaxableLine> lines, IReadOnlyDictionary<int, decimal> shopDiscounts, Func<int, decimal> rateFor, int decimalPlaces)
    {
        var taxed = new TaxedLine[lines.Count];
        foreach (var shop in lines.Select((line, index) => (line, index)).GroupBy(x => x.line.VendorId))
        {
            var discount = shopDiscounts.GetValueOrDefault(shop.Key);
            var shares = discount > 0m
                ? DiscountRules.Split(discount, shop.ToDictionary(x => x.index, x => x.line.LineTotal), decimalPlaces)
                : new Dictionary<int, decimal>();

            foreach (var (line, index) in shop)
            {
                var baseAmount = Math.Max(0m, line.LineTotal - shares.GetValueOrDefault(index));
                var rate = rateFor(line.ProductId);
                var tax = Math.Round(baseAmount * rate / 100m, decimalPlaces, MidpointRounding.AwayFromZero);
                taxed[index] = new TaxedLine(line.VendorId, line.ProductId, rate, baseAmount, tax);
            }
        }

        var perShop = taxed.GroupBy(t => t.VendorId).ToDictionary(g => g.Key, g => g.Sum(t => t.Tax));
        return new TaxCalculation(taxed, perShop, perShop.Values.Sum());
    }
}

public interface ITaxStore
{
    Task<IReadOnlyList<TaxCategory>> GetCategoriesAsync(CancellationToken cancellationToken);
    Task<TaxCategory?> GetCategoryAsync(int id, CancellationToken cancellationToken);

    /// <summary>Adds the category; 0 (and nothing added) when the name is already used.</summary>
    Task<int> InsertCategoryAsync(TaxCategory category, CancellationToken cancellationToken);

    /// <summary>Writes the name and the order; false when the name now belongs to another category.</summary>
    Task<bool> UpdateCategoryAsync(TaxCategory category, CancellationToken cancellationToken);

    Task DeleteCategoryAsync(int id, CancellationToken cancellationToken);
    Task<int> CountRatesAsync(int categoryId, CancellationToken cancellationToken);
    Task<int> CountProductsAsync(int categoryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxRate>> GetRatesAsync(int categoryId, CancellationToken cancellationToken);
    Task<TaxRate?> GetRateAsync(int id, CancellationToken cancellationToken);

    /// <summary>Published rates of every category for one country, for checkout.</summary>
    Task<IReadOnlyList<TaxRate>> GetPublishedRatesAsync(string countryCode, CancellationToken cancellationToken);

    /// <summary>Adds the rate; 0 (and nothing added) when the category already has a rate for the place.</summary>
    Task<int> InsertRateAsync(TaxRate rate, CancellationToken cancellationToken);

    /// <summary>Writes the place, the percentage and the switch; false when another rate of the category has the place.</summary>
    Task<bool> UpdateRateAsync(TaxRate rate, CancellationToken cancellationToken);

    Task DeleteRateAsync(int id, CancellationToken cancellationToken);

    /// <summary>The category assigned to each product; products without an assignment are not in the answer.</summary>
    Task<IReadOnlyDictionary<int, int>> GetProductCategoryIdsAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken);

    /// <summary>Assigns the category, or clears the assignment when it is null.</summary>
    Task SetProductCategoryAsync(int productId, int? categoryId, CancellationToken cancellationToken);
}

public interface ITaxService
{
    // ---- Administrators ----

    Task<IReadOnlyList<TaxCategory>> GetCategoriesAsync(CancellationToken cancellationToken);
    Task<CatalogResult<TaxCategory>> CreateCategoryAsync(SaveTaxCategoryCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<TaxCategory>> UpdateCategoryAsync(int id, SaveTaxCategoryCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteCategoryAsync(int id, int actorCustomerId, CancellationToken cancellationToken);

    Task<CatalogResult<IReadOnlyList<TaxRate>>> GetRatesAsync(int categoryId, CancellationToken cancellationToken);
    Task<CatalogResult<TaxRate>> CreateRateAsync(int categoryId, SaveTaxRateCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<TaxRate>> UpdateRateAsync(int id, SaveTaxRateCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteRateAsync(int id, int actorCustomerId, CancellationToken cancellationToken);

    Task<CatalogResult<ProductTaxView>> GetProductAsync(int productId, CancellationToken cancellationToken);

    /// <summary>Assigns the category of a product; null returns it to the default.</summary>
    Task<CatalogResult<ProductTaxView>> SetProductCategoryAsync(int productId, int? taxCategoryId, int actorCustomerId, CancellationToken cancellationToken);

    // ---- Checkout ----

    /// <summary>The tax of the lines for a destination, after the discount of each shop. Changes nothing.</summary>
    Task<TaxCalculation> CalculateAsync(
        string countryCode, int? stateProvinceId, IReadOnlyList<TaxableLine> lines, IReadOnlyDictionary<int, decimal> shopDiscounts, CancellationToken cancellationToken);
}
