using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Directory;

/// <summary>Business-rule codes of the directory module (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class DirectoryErrors
{
    public const string CurrencyPrimaryLocked = "currency.primary_locked";
    public const string CurrencyPrimaryRequired = "currency.primary_required";
    public const string CurrencyCodeExists = "currency.code_exists";
    public const string CountryCodeExists = "country.code_exists";
    public const string CountryInUse = "country.in_use";
    public const string StateCodeExists = "state.code_exists";
    public const string StateInUse = "state.in_use";
}

public static class CurrencyLimits
{
    public const int MaxDecimalPlaces = 4;
    public const int MaxNameLength = 100;
    public const int MaxSymbolLength = 10;
    public const decimal MaxRate = 1_000_000_000m;
    public const int RateScale = 8;
}

public sealed class Currency
{
    public int Id { get; set; }

    /// <summary>Three letters A to Z, upper case. Never changes.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>What is shown before the amount; the code is used when empty.</summary>
    public string? Symbol { get; set; }

    public int DecimalPlaces { get; set; } = 2;

    /// <summary>Units of this currency for one unit of the primary currency. Exactly 1 for the primary.</summary>
    public decimal RateToPrimary { get; set; } = 1m;

    public bool IsPrimary { get; set; }
    public bool Published { get; set; } = true;
    public int DisplayOrder { get; set; }
    public DateTime RateUpdatedOnUtc { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

/// <summary>The money arithmetic. Pure, so every rule can be tested without a database.</summary>
public static class CurrencyRules
{
    /// <summary>Rounds half away from zero to the given decimals (2.5 becomes 3, -2.5 becomes -3).</summary>
    public static decimal Round(decimal amount, int decimalPlaces) =>
        Math.Round(amount, decimalPlaces, MidpointRounding.AwayFromZero);

    /// <summary>Converts through the primary currency and rounds to the target's decimals. Used for display only.</summary>
    public static decimal Convert(decimal amount, Currency from, Currency to) =>
        Round(amount / from.RateToPrimary * to.RateToPrimary, to.DecimalPlaces);

    /// <summary>The number of decimals an amount really needs: 12.50 has 1, 12.000 has 0.</summary>
    public static int DecimalPlacesOf(decimal amount)
    {
        var scale = (decimal.GetBits(amount)[3] >> 16) & 0xFF;
        // 12.500 is stored with scale 3; drop the trailing zeros.
        while (scale > 0 && Math.Round(amount, scale - 1) == amount) scale--;
        return scale;
    }

    public static bool HasValidScale(decimal amount, int decimalPlaces) => DecimalPlacesOf(amount) <= decimalPlaces;

    /// <summary>A currency's rate after another currency became the primary one: rate divided by the new primary's old rate.</summary>
    public static decimal Rebase(decimal rate, decimal newPrimaryOldRate) =>
        Math.Round(rate / newPrimaryOldRate, CurrencyLimits.RateScale, MidpointRounding.AwayFromZero);
}

public sealed record SaveCurrencyCommand(
    string? Code, string? Name, string? Symbol, int DecimalPlaces, decimal Rate, bool Published, int DisplayOrder);

public interface ICurrencyStore
{
    Task<IReadOnlyList<Currency>> GetAllAsync(CancellationToken cancellationToken);
    Task<Currency?> GetAsync(int id, CancellationToken cancellationToken);
    Task<Currency?> GetByCodeAsync(string code, CancellationToken cancellationToken);
    Task<Currency?> GetPrimaryAsync(CancellationToken cancellationToken);
    Task<int> InsertAsync(Currency currency, CancellationToken cancellationToken);

    /// <summary>Writes name, symbol, decimals, rate, published, order and the rate time. The code and the primary flag are never changed here.</summary>
    Task UpdateAsync(Currency currency, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);

    /// <summary>In one transaction: applies the new rates, clears the old primary flag and sets the flag (rate 1, published) on <paramref name="id"/>.</summary>
    Task MakePrimaryAsync(int id, IReadOnlyDictionary<int, decimal> newRates, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>True when any product row exists, deleted ones included.</summary>
    Task<bool> AnyProductAsync(CancellationToken cancellationToken);
}

public interface ICurrencyService
{
    Task<IReadOnlyList<Currency>> GetPublishedAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Currency>> GetAllAsync(CancellationToken cancellationToken);
    Task<CatalogResult<Currency>> CreateAsync(SaveCurrencyCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<Currency>> UpdateAsync(int id, SaveCurrencyCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteAsync(int id, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<Currency>> MakePrimaryAsync(int id, int actorCustomerId, CancellationToken cancellationToken);
}

/// <summary>The currency every stored amount is in. Services that accept prices use it to check the number of decimals.</summary>
public interface IPrimaryCurrencyProvider
{
    Task<Currency> GetPrimaryAsync(CancellationToken cancellationToken);
}
