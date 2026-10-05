using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Shipping;

public sealed class ShippingService(
    IShippingStore store,
    IDirectoryService directory,
    ICartService cartService,
    ICustomerAccountDataService addressService,
    IPrimaryCurrencyProvider primaryCurrency,
    IAuditLogService auditLog,
    IClock clock) : IShippingService
{
    // ---- Shop members ----

    public Task<IReadOnlyList<ShippingRate>> GetRatesAsync(int vendorId, CancellationToken cancellationToken) =>
        store.GetRatesAsync(vendorId, cancellationToken);

    public async Task<CatalogResult<ShippingRate>> CreateRateAsync(
        int vendorId, SaveShippingRateCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = await ValidateAsync(command, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<ShippingRate>(errors);

        if (await store.CountRatesAsync(vendorId, cancellationToken) >= ShippingLimits.MaxRatesPerShop)
            return CatalogResult.Error<ShippingRate>(ShippingErrors.RateLimit);

        var now = clock.UtcNow;
        var rate = new ShippingRate { VendorId = vendorId, CreatedOnUtc = now };
        Apply(rate, command, now);
        rate.Id = await store.InsertAsync(rate, cancellationToken);

        await auditLog.WriteAsync("shipping.rate_created", actorCustomerId, entityType: "ShippingRate", entityId: rate.Id,
            details: new { vendorId, rateId = rate.Id, rate.CountryCode }, cancellationToken: cancellationToken);
        return CatalogResult.Success(rate);
    }

    public async Task<CatalogResult<ShippingRate>> UpdateRateAsync(
        int vendorId, int id, SaveShippingRateCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var rate = await store.GetRateAsync(vendorId, id, cancellationToken);
        if (rate is null) return CatalogResult.Error<ShippingRate>(CatalogErrors.NotFound);

        var errors = await ValidateAsync(command, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<ShippingRate>(errors);

        Apply(rate, command, clock.UtcNow);
        await store.UpdateAsync(rate, cancellationToken);

        await auditLog.WriteAsync("shipping.rate_updated", actorCustomerId, entityType: "ShippingRate", entityId: id,
            details: new { vendorId, rateId = id, rate.CountryCode, rate.Published }, cancellationToken: cancellationToken);
        return CatalogResult.Success(rate);
    }

    public async Task<CatalogResult<bool>> DeleteRateAsync(int vendorId, int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var rate = await store.GetRateAsync(vendorId, id, cancellationToken);
        if (rate is null || !await store.DeleteAsync(vendorId, id, cancellationToken)) return CatalogResult.Error<bool>(CatalogErrors.NotFound);

        await auditLog.WriteAsync("shipping.rate_deleted", actorCustomerId, entityType: "ShippingRate", entityId: id,
            details: new { vendorId, rateId = id, rate.CountryCode }, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }

    // ---- Customers ----

    public async Task<CatalogResult<ShippingQuote>> QuoteAsync(int customerId, ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        string countryCode;
        int? stateId;
        if (request.AddressId is { } addressId)
        {
            // Only the customer's own addresses are searched, so another customer's address is simply not found.
            var address = (await addressService.GetAddressesAsync(customerId, cancellationToken)).FirstOrDefault(a => a.Id == addressId);
            if (address is null) return CatalogResult.Error<ShippingQuote>(CatalogErrors.NotFound);
            (countryCode, stateId) = (address.CountryCode, address.StateProvinceId);

            var resolved = await ResolveDestinationAsync(countryCode, stateId, cancellationToken);
            if (resolved.Count > 0)
                return CatalogResult.Failure<ShippingQuote>("addressId", "We cannot ship to this address. Choose another one.");
        }
        else
        {
            countryCode = request.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
            stateId = request.StateProvinceId;
            var errors = await ResolveDestinationAsync(countryCode, stateId, cancellationToken);
            if (errors.Count > 0) return CatalogResult.Failure<ShippingQuote>(errors);
        }

        // Free shipping over an amount counts only what is being bought now.
        var cart = CartRules.Narrow(await cartService.GetAsync(customerId, cancellationToken), request.CartItemIds);
        var rates = await store.GetPublishedRatesAsync(cart.Groups.Select(g => g.VendorId).ToList(), countryCode, cancellationToken);

        var shops = cart.Groups.Select(group =>
        {
            var options = ShippingRules.Options(rates.Where(r => r.VendorId == group.VendorId), countryCode, stateId, group.Subtotal);
            return new ShippingShopQuote(group.VendorId, group.VendorName, group.Subtotal, options, options.Count > 0);
        }).ToList();

        return CatalogResult.Success(new ShippingQuote(
            cart.CurrencyCode, countryCode, stateId, shops, shops.Count > 0 && shops.All(s => s.CanShip), ShippingRules.ShippingTotal(shops)));
    }

    // ---- Helpers ----

    /// <summary>The country has to be open for shipping; a country with states needs one of them. Empty when the destination is fine.</summary>
    private async Task<Dictionary<string, string[]>> ResolveDestinationAsync(string countryCode, int? stateId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var country = (await directory.GetPublishedCountriesAsync(cancellationToken))
            .FirstOrDefault(c => string.Equals(c.Code, countryCode, StringComparison.OrdinalIgnoreCase));
        if (country is null || !country.AllowsShipping)
        {
            errors["countryCode"] = ["We do not ship to this country."];
            return errors;
        }

        var states = await directory.GetPublishedStatesAsync(country.Code, cancellationToken) ?? [];
        if (states.Count > 0)
        {
            if (stateId is null) errors["stateProvinceId"] = ["Choose a state."];
            else if (states.All(s => s.Id != stateId)) errors["stateProvinceId"] = ["Choose a state of this country."];
        }
        else if (stateId is not null)
        {
            errors["stateProvinceId"] = ["This country has no states."];
        }
        return errors;
    }

    private async Task<Dictionary<string, string[]>> ValidateAsync(SaveShippingRateCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) errors["name"] = ["Enter a name."];
        else if (name.Length > ShippingLimits.MaxNameLength) errors["name"] = [$"The name can have at most {ShippingLimits.MaxNameLength} characters."];

        var places = (await primaryCurrency.GetPrimaryAsync(cancellationToken)).DecimalPlaces;
        if (command.Fee is < 0 or > ShippingLimits.MaxAmount)
            errors["fee"] = [$"The fee must be between 0 and {ShippingLimits.MaxAmount:0}."];
        else if (!CurrencyRules.HasValidScale(command.Fee, places))
            errors["fee"] = [$"The fee can have at most {places} decimal places."];

        if (command.FreeOverSubtotal is { } freeOver)
        {
            if (freeOver is <= 0 or > ShippingLimits.MaxAmount)
                errors["freeOverSubtotal"] = [$"The amount must be above 0 and at most {ShippingLimits.MaxAmount:0}."];
            else if (!CurrencyRules.HasValidScale(freeOver, places))
                errors["freeOverSubtotal"] = [$"The amount can have at most {places} decimal places."];
        }

        foreach (var (field, days) in new[] { ("minDays", command.MinDays), ("maxDays", command.MaxDays) })
        {
            if (days is < 0 or > ShippingLimits.MaxDays) errors[field] = [$"Days must be between 0 and {ShippingLimits.MaxDays}."];
        }
        if (!errors.ContainsKey("minDays") && !errors.ContainsKey("maxDays") && command.MinDays > command.MaxDays)
            errors["minDays"] = ["The minimum cannot be above the maximum."];

        // Only a country that is open for shipping can have a rate, and a state has to belong to it.
        var countries = await directory.GetPublishedCountriesAsync(cancellationToken);
        var code = command.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var country = countries.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        if (country is null || !country.AllowsShipping)
        {
            errors["countryCode"] = ["Choose a country that is open for shipping."];
        }
        else if (command.StateProvinceId is { } stateId)
        {
            var states = await directory.GetPublishedStatesAsync(country.Code, cancellationToken) ?? [];
            if (states.All(s => s.Id != stateId)) errors["stateProvinceId"] = ["Choose a state of this country."];
        }
        return errors;
    }

    private static void Apply(ShippingRate rate, SaveShippingRateCommand command, DateTime now)
    {
        rate.Name = command.Name!.Trim();
        rate.CountryCode = command.CountryCode!.Trim().ToUpperInvariant();
        rate.StateProvinceId = command.StateProvinceId;
        rate.Fee = command.Fee;
        rate.FreeOverSubtotal = command.FreeOverSubtotal;
        rate.MinDays = command.MinDays;
        rate.MaxDays = command.MaxDays;
        rate.Published = command.Published;
        rate.DisplayOrder = command.DisplayOrder;
        rate.UpdatedOnUtc = now;
    }
}
