using System.Text.RegularExpressions;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Directory;

public sealed partial class CurrencyService(
    ICurrencyStore store,
    IAuditLogService auditLog,
    IClock clock) : ICurrencyService
{
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CodePattern();

    public async Task<IReadOnlyList<Currency>> GetPublishedAsync(CancellationToken cancellationToken) =>
        Ordered((await store.GetAllAsync(cancellationToken)).Where(c => c.Published));

    public async Task<IReadOnlyList<Currency>> GetAllAsync(CancellationToken cancellationToken) =>
        Ordered(await store.GetAllAsync(cancellationToken));

    public async Task<CatalogResult<Currency>> CreateAsync(SaveCurrencyCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = Validate(command, creating: true);
        var code = command.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (errors.Count > 0) return CatalogResult.Failure<Currency>(errors);
        if (await store.GetByCodeAsync(code, cancellationToken) is not null)
            return CatalogResult.Error<Currency>(DirectoryErrors.CurrencyCodeExists);

        var now = clock.UtcNow;
        var currency = new Currency
        {
            Code = code,
            Name = command.Name!.Trim(),
            Symbol = NullIfBlank(command.Symbol),
            DecimalPlaces = command.DecimalPlaces,
            RateToPrimary = CurrencyRules.Round(command.Rate, CurrencyLimits.RateScale),
            IsPrimary = false,
            Published = command.Published,
            DisplayOrder = command.DisplayOrder,
            RateUpdatedOnUtc = now,
            CreatedOnUtc = now,
            UpdatedOnUtc = now
        };
        currency.Id = await store.InsertAsync(currency, cancellationToken);

        await auditLog.WriteAsync("currency.created", actorCustomerId, entityType: "Currency", entityId: currency.Id,
            details: new { currencyId = currency.Id, code, decimalPlaces = currency.DecimalPlaces, rate = currency.RateToPrimary }, cancellationToken: cancellationToken);
        return CatalogResult.Success(currency);
    }

    public async Task<CatalogResult<Currency>> UpdateAsync(int id, SaveCurrencyCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var currency = await store.GetAsync(id, cancellationToken);
        if (currency is null) return CatalogResult.Error<Currency>(CatalogErrors.NotFound);

        // The primary is exactly 1 and always published, so only the other fields are checked for it.
        var errors = Validate(command, creating: false, skipRate: currency.IsPrimary);
        if (errors.Count > 0) return CatalogResult.Failure<Currency>(errors);

        if (currency.IsPrimary)
        {
            if (!command.Published) return CatalogResult.Error<Currency>(DirectoryErrors.CurrencyPrimaryRequired);
            // Stored prices were written with these decimals; changing them would change what the numbers mean.
            if (command.DecimalPlaces != currency.DecimalPlaces && await store.AnyProductAsync(cancellationToken))
                return CatalogResult.Error<Currency>(DirectoryErrors.CurrencyPrimaryLocked);
        }

        var now = clock.UtcNow;
        var previousRate = currency.RateToPrimary;
        currency.Name = command.Name!.Trim();
        currency.Symbol = NullIfBlank(command.Symbol);
        currency.DecimalPlaces = command.DecimalPlaces;
        currency.DisplayOrder = command.DisplayOrder;
        currency.Published = currency.IsPrimary || command.Published;
        if (!currency.IsPrimary)
        {
            currency.RateToPrimary = CurrencyRules.Round(command.Rate, CurrencyLimits.RateScale);
            if (currency.RateToPrimary != previousRate) currency.RateUpdatedOnUtc = now;
        }
        currency.UpdatedOnUtc = now;
        await store.UpdateAsync(currency, cancellationToken);

        await auditLog.WriteAsync("currency.updated", actorCustomerId, entityType: "Currency", entityId: id,
            details: new { currencyId = id, code = currency.Code, decimalPlaces = currency.DecimalPlaces, rate = currency.RateToPrimary, previousRate, currency.Published },
            cancellationToken: cancellationToken);
        return CatalogResult.Success(currency);
    }

    public async Task<CatalogResult<bool>> DeleteAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var currency = await store.GetAsync(id, cancellationToken);
        if (currency is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);
        if (currency.IsPrimary) return CatalogResult.Error<bool>(DirectoryErrors.CurrencyPrimaryRequired);

        await store.DeleteAsync(id, cancellationToken);
        await auditLog.WriteAsync("currency.deleted", actorCustomerId, entityType: "Currency", entityId: id,
            details: new { currencyId = id, code = currency.Code }, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }

    public async Task<CatalogResult<Currency>> MakePrimaryAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var target = await store.GetAsync(id, cancellationToken);
        if (target is null) return CatalogResult.Error<Currency>(CatalogErrors.NotFound);
        if (target.IsPrimary) return CatalogResult.Success(target);

        // Stored prices are in the old primary currency: switching would silently change what they mean.
        if (await store.AnyProductAsync(cancellationToken)) return CatalogResult.Error<Currency>(DirectoryErrors.CurrencyPrimaryLocked);

        var all = await store.GetAllAsync(cancellationToken);
        var previous = all.FirstOrDefault(c => c.IsPrimary);
        var newRates = all.ToDictionary(c => c.Id, c => c.Id == id ? 1m : CurrencyRules.Rebase(c.RateToPrimary, target.RateToPrimary));

        var now = clock.UtcNow;
        await store.MakePrimaryAsync(id, newRates, now, cancellationToken);

        await auditLog.WriteAsync("currency.primary_changed", actorCustomerId, entityType: "Currency", entityId: id,
            details: new { currencyId = id, code = target.Code, previousCurrencyId = previous?.Id, previousCode = previous?.Code }, cancellationToken: cancellationToken);
        return CatalogResult.Success((await store.GetAsync(id, cancellationToken))!);
    }

    // ---- Helpers ----

    private static List<Currency> Ordered(IEnumerable<Currency> currencies) =>
        currencies.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.DisplayOrder).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();

    private static Dictionary<string, string[]> Validate(SaveCurrencyCommand command, bool creating, bool skipRate = false)
    {
        var errors = new Dictionary<string, string[]>();
        if (creating && !CodePattern().IsMatch(command.Code?.Trim().ToUpperInvariant() ?? string.Empty))
            errors["code"] = ["The code must be three letters, for example USD."];

        var name = command.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > CurrencyLimits.MaxNameLength)
            errors["name"] = [$"The name must be 1 to {CurrencyLimits.MaxNameLength} characters."];
        if (NullIfBlank(command.Symbol) is { Length: > CurrencyLimits.MaxSymbolLength })
            errors["symbol"] = [$"The symbol cannot exceed {CurrencyLimits.MaxSymbolLength} characters."];
        if (command.DecimalPlaces is < 0 or > CurrencyLimits.MaxDecimalPlaces)
            errors["decimalPlaces"] = [$"Decimal places must be between 0 and {CurrencyLimits.MaxDecimalPlaces}."];
        if (!skipRate && (command.Rate <= 0 || command.Rate > CurrencyLimits.MaxRate || CurrencyRules.Round(command.Rate, CurrencyLimits.RateScale) <= 0))
            errors["rate"] = [$"The rate must be above 0 and at most {CurrencyLimits.MaxRate:N0}."];
        return errors;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Reads the primary currency for services that validate prices. Falls back to USD only if the table has no primary (a broken install).</summary>
public sealed class PrimaryCurrencyProvider(ICurrencyStore store) : IPrimaryCurrencyProvider
{
    public async Task<Currency> GetPrimaryAsync(CancellationToken cancellationToken) =>
        await store.GetPrimaryAsync(cancellationToken)
        ?? new Currency { Code = "USD", Name = "US Dollar", Symbol = "$", DecimalPlaces = 2, IsPrimary = true };
}
