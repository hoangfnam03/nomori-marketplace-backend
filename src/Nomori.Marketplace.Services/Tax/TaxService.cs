using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Tax;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Tax;

public sealed class TaxService(
    ITaxStore store,
    IProductStore productStore,
    IDirectoryService directory,
    IPrimaryCurrencyProvider primaryCurrency,
    IAuditLogService auditLog,
    IClock clock) : ITaxService
{
    // ---- Categories ----

    public Task<IReadOnlyList<TaxCategory>> GetCategoriesAsync(CancellationToken cancellationToken) => store.GetCategoriesAsync(cancellationToken);

    public async Task<CatalogResult<TaxCategory>> CreateCategoryAsync(SaveTaxCategoryCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = ValidateCategory(command);
        if (errors.Count > 0) return CatalogResult.Failure<TaxCategory>(errors);
        if ((await store.GetCategoriesAsync(cancellationToken)).Count >= TaxLimits.MaxCategories) return CatalogResult.Error<TaxCategory>(TaxErrors.CategoryLimit);

        var now = clock.UtcNow;
        var category = new TaxCategory { Name = command.Name!.Trim(), DisplayOrder = command.DisplayOrder, CreatedOnUtc = now, UpdatedOnUtc = now };
        category.Id = await store.InsertCategoryAsync(category, cancellationToken);
        if (category.Id == 0) return CatalogResult.Error<TaxCategory>(TaxErrors.CategoryExists);

        await auditLog.WriteAsync("tax.category_created", actorCustomerId, entityType: "TaxCategory", entityId: category.Id,
            details: new { categoryId = category.Id, category.Name }, cancellationToken: cancellationToken);
        return CatalogResult.Success(category);
    }

    public async Task<CatalogResult<TaxCategory>> UpdateCategoryAsync(int id, SaveTaxCategoryCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var category = await store.GetCategoryAsync(id, cancellationToken);
        if (category is null) return CatalogResult.Error<TaxCategory>(CatalogErrors.NotFound);

        var errors = ValidateCategory(command);
        if (errors.Count > 0) return CatalogResult.Failure<TaxCategory>(errors);

        category.Name = command.Name!.Trim();
        category.DisplayOrder = command.DisplayOrder;
        category.UpdatedOnUtc = clock.UtcNow;
        if (!await store.UpdateCategoryAsync(category, cancellationToken)) return CatalogResult.Error<TaxCategory>(TaxErrors.CategoryExists);

        await auditLog.WriteAsync("tax.category_updated", actorCustomerId, entityType: "TaxCategory", entityId: id,
            details: new { categoryId = id, category.Name }, cancellationToken: cancellationToken);
        return CatalogResult.Success(category);
    }

    public async Task<CatalogResult<bool>> DeleteCategoryAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var category = await store.GetCategoryAsync(id, cancellationToken);
        if (category is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);

        // Products without an assignment are in the default category, so it has to stay.
        if (category.IsDefault) return CatalogResult.Error<bool>(TaxErrors.DefaultCategory);
        if (await store.CountRatesAsync(id, cancellationToken) > 0 || await store.CountProductsAsync(id, cancellationToken) > 0)
            return CatalogResult.Error<bool>(TaxErrors.CategoryInUse);

        await store.DeleteCategoryAsync(id, cancellationToken);
        await auditLog.WriteAsync("tax.category_deleted", actorCustomerId, entityType: "TaxCategory", entityId: id,
            details: new { categoryId = id, category.Name }, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }

    // ---- Rates ----

    public async Task<CatalogResult<IReadOnlyList<TaxRate>>> GetRatesAsync(int categoryId, CancellationToken cancellationToken) =>
        await store.GetCategoryAsync(categoryId, cancellationToken) is null
            ? CatalogResult.Error<IReadOnlyList<TaxRate>>(CatalogErrors.NotFound)
            : CatalogResult.Success(await store.GetRatesAsync(categoryId, cancellationToken));

    public async Task<CatalogResult<TaxRate>> CreateRateAsync(int categoryId, SaveTaxRateCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (await store.GetCategoryAsync(categoryId, cancellationToken) is null) return CatalogResult.Error<TaxRate>(CatalogErrors.NotFound);

        var errors = await ValidateRateAsync(command, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<TaxRate>(errors);
        if (await store.CountRatesAsync(categoryId, cancellationToken) >= TaxLimits.MaxRatesPerCategory) return CatalogResult.Error<TaxRate>(TaxErrors.RateLimit);

        var now = clock.UtcNow;
        var rate = new TaxRate { CategoryId = categoryId, CreatedOnUtc = now };
        Apply(rate, command, now);
        rate.Id = await store.InsertRateAsync(rate, cancellationToken);
        if (rate.Id == 0) return CatalogResult.Error<TaxRate>(TaxErrors.RateExists);

        await auditLog.WriteAsync("tax.rate_created", actorCustomerId, entityType: "TaxRate", entityId: rate.Id,
            details: new { rateId = rate.Id, categoryId, rate.CountryCode, rate.StateProvinceId, rate.Percentage }, cancellationToken: cancellationToken);
        return CatalogResult.Success(rate);
    }

    public async Task<CatalogResult<TaxRate>> UpdateRateAsync(int id, SaveTaxRateCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var rate = await store.GetRateAsync(id, cancellationToken);
        if (rate is null) return CatalogResult.Error<TaxRate>(CatalogErrors.NotFound);

        var errors = await ValidateRateAsync(command, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<TaxRate>(errors);

        Apply(rate, command, clock.UtcNow);
        if (!await store.UpdateRateAsync(rate, cancellationToken)) return CatalogResult.Error<TaxRate>(TaxErrors.RateExists);

        await auditLog.WriteAsync("tax.rate_updated", actorCustomerId, entityType: "TaxRate", entityId: id,
            details: new { rateId = id, rate.CategoryId, rate.CountryCode, rate.StateProvinceId, rate.Percentage, rate.Published }, cancellationToken: cancellationToken);
        return CatalogResult.Success(rate);
    }

    public async Task<CatalogResult<bool>> DeleteRateAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var rate = await store.GetRateAsync(id, cancellationToken);
        if (rate is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);

        await store.DeleteRateAsync(id, cancellationToken);
        await auditLog.WriteAsync("tax.rate_deleted", actorCustomerId, entityType: "TaxRate", entityId: id,
            details: new { rateId = id, rate.CategoryId, rate.CountryCode, rate.StateProvinceId }, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }

    // ---- Products ----

    public async Task<CatalogResult<ProductTaxView>> GetProductAsync(int productId, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null) return CatalogResult.Error<ProductTaxView>(CatalogErrors.NotFound);

        var categories = await store.GetCategoriesAsync(cancellationToken);
        var assigned = (await store.GetProductCategoryIdsAsync([productId], cancellationToken)).TryGetValue(productId, out var id) ? categories.FirstOrDefault(c => c.Id == id) : null;
        var category = assigned ?? categories.First(c => c.IsDefault);
        return CatalogResult.Success(new ProductTaxView(productId, product.Name, category.Id, category.Name, assigned is not null));
    }

    public async Task<CatalogResult<ProductTaxView>> SetProductCategoryAsync(int productId, int? taxCategoryId, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (await productStore.GetAsync(productId, cancellationToken) is null) return CatalogResult.Error<ProductTaxView>(CatalogErrors.NotFound);
        if (taxCategoryId is { } id && await store.GetCategoryAsync(id, cancellationToken) is null)
            return CatalogResult.Failure<ProductTaxView>("taxCategoryId", "Choose a tax category.");

        await store.SetProductCategoryAsync(productId, taxCategoryId, cancellationToken);
        await auditLog.WriteAsync("tax.product_assigned", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, taxCategoryId }, cancellationToken: cancellationToken);
        return await GetProductAsync(productId, cancellationToken);
    }

    // ---- Checkout ----

    public async Task<TaxCalculation> CalculateAsync(
        string countryCode, int? stateProvinceId, IReadOnlyList<TaxableLine> lines, IReadOnlyDictionary<int, decimal> shopDiscounts, CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return TaxCalculation.None;

        var places = (await primaryCurrency.GetPrimaryAsync(cancellationToken)).DecimalPlaces;
        var defaultId = (await store.GetCategoriesAsync(cancellationToken)).First(c => c.IsDefault).Id;
        var assigned = await store.GetProductCategoryIdsAsync(lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken);
        var rates = await store.GetPublishedRatesAsync(countryCode.Trim().ToUpperInvariant(), cancellationToken);

        return TaxRules.Calculate(lines, shopDiscounts,
            productId => TaxRules.FindRate(rates, assigned.GetValueOrDefault(productId, defaultId), countryCode, stateProvinceId), places);
    }

    // ---- Helpers ----

    private static Dictionary<string, string[]> ValidateCategory(SaveTaxCategoryCommand command)
    {
        var errors = new Dictionary<string, string[]>();
        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > TaxLimits.MaxNameLength) errors["name"] = [$"Enter a name of 1 to {TaxLimits.MaxNameLength} characters."];
        return errors;
    }

    private async Task<Dictionary<string, string[]>> ValidateRateAsync(SaveTaxRateCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (command.Percentage is < 0 or > TaxLimits.MaxPercentage || CurrencyRules.DecimalPlacesOf(command.Percentage) > TaxLimits.PercentageDecimals)
            errors["percentage"] = [$"The rate must be between 0 and 100, with at most {TaxLimits.PercentageDecimals} decimal places."];

        var code = command.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var country = (await directory.GetPublishedCountriesAsync(cancellationToken)).FirstOrDefault(c => c.Code == code);
        if (country is null)
        {
            errors["countryCode"] = ["Choose a country from the directory."];
        }
        else if (command.StateProvinceId is { } stateId)
        {
            var states = await directory.GetPublishedStatesAsync(country.Code, cancellationToken) ?? [];
            if (states.All(s => s.Id != stateId)) errors["stateProvinceId"] = ["Choose a state of this country."];
        }
        return errors;
    }

    private static void Apply(TaxRate rate, SaveTaxRateCommand command, DateTime now)
    {
        rate.CountryCode = command.CountryCode!.Trim().ToUpperInvariant();
        rate.StateProvinceId = command.StateProvinceId;
        rate.Percentage = command.Percentage;
        rate.Published = command.Published;
        rate.UpdatedOnUtc = now;
    }
}
