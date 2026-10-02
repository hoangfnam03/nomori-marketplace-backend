using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Directory;

public sealed class DirectoryService(
    IDirectoryStore store,
    IAuditLogService auditLog,
    IClock clock) : IDirectoryService
{
    // ---- Public ----

    public async Task<IReadOnlyList<Country>> GetPublishedCountriesAsync(CancellationToken cancellationToken) =>
        Ordered((await store.GetCountriesAsync(cancellationToken)).Where(c => c.Published));

    public async Task<IReadOnlyList<StateProvince>?> GetPublishedStatesAsync(string countryCode, CancellationToken cancellationToken)
    {
        var country = await store.GetCountryByCodeAsync(countryCode?.Trim().ToUpperInvariant() ?? string.Empty, cancellationToken);
        if (country is not { Published: true }) return null;
        return Ordered((await store.GetStatesAsync(country.Id, cancellationToken)).Where(s => s.Published));
    }

    // ---- Address rules ----

    public async Task<CatalogResult<ResolvedAddressLocation>> ResolveAddressAsync(
        string? countryCode, int? stateProvinceId, string? stateText, string? postalCode, AddressUse use, CancellationToken cancellationToken)
    {
        var code = countryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (code.Length != 2 || !code.All(char.IsAsciiLetter))
            return CatalogResult.Failure<ResolvedAddressLocation>("countryCode", "Choose a country.");

        var country = await store.GetCountryByCodeAsync(code, cancellationToken);
        if (country is null || !country.CanBeUsedFor(use))
            return CatalogResult.Failure<ResolvedAddressLocation>("countryCode", "We cannot use this country for an address.");

        var errors = new Dictionary<string, string[]>();

        // With states in the directory the state has to be one of them; without, it is optional free text.
        var states = (await store.GetStatesAsync(country.Id, cancellationToken)).Where(s => s.Published).ToList();
        int? resolvedStateId = null;
        string? stateName;
        if (states.Count > 0)
        {
            var state = stateProvinceId is { } id ? states.FirstOrDefault(s => s.Id == id) : null;
            if (state is null) errors["stateProvinceId"] = [stateProvinceId is null ? "Choose a state or province." : "This state or province does not belong to the country."];
            resolvedStateId = state?.Id;
            stateName = state?.Name;
        }
        else
        {
            if (stateProvinceId is not null) errors["stateProvinceId"] = ["This country has no states or provinces."];
            stateName = string.IsNullOrWhiteSpace(stateText) ? null : stateText.Trim();
            if (stateName is { Length: > CountryLimits.MaxStateTextLength })
                errors["stateProvince"] = [$"The state or province cannot exceed {CountryLimits.MaxStateTextLength} characters."];
        }

        var zip = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
        if (zip is null)
        {
            if (country.PostalCodeRequired) errors["zipPostalCode"] = [$"A postal code is required for {country.Name}."];
        }
        else if (zip.Length > CountryLimits.MaxPostalCodeLength)
        {
            errors["zipPostalCode"] = [$"The postal code cannot exceed {CountryLimits.MaxPostalCodeLength} characters."];
        }
        else if (!string.IsNullOrWhiteSpace(country.PostalCodePattern) && !PostalCodeRules.Matches(country.PostalCodePattern, zip))
        {
            errors["zipPostalCode"] = [$"The postal code is not valid for {country.Name}."];
        }

        return errors.Count > 0
            ? CatalogResult.Failure<ResolvedAddressLocation>(errors)
            : CatalogResult.Success(new ResolvedAddressLocation(country.Code, resolvedStateId, stateName, zip));
    }

    // ---- Administrators: countries ----

    public async Task<IReadOnlyList<Country>> GetCountriesAsync(CancellationToken cancellationToken) =>
        Ordered(await store.GetCountriesAsync(cancellationToken));

    public async Task<CatalogResult<Country>> CreateCountryAsync(SaveCountryCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = ValidateCountry(command, creating: true);
        if (errors.Count > 0) return CatalogResult.Failure<Country>(errors);

        var code = command.Code!.Trim().ToUpperInvariant();
        if (await store.GetCountryByCodeAsync(code, cancellationToken) is not null)
            return CatalogResult.Error<Country>(DirectoryErrors.CountryCodeExists);

        var now = clock.UtcNow;
        var country = new Country { Code = code, CreatedOnUtc = now };
        Apply(country, command, now);
        country.Id = await store.InsertCountryAsync(country, cancellationToken);

        await auditLog.WriteAsync("country.created", actorCustomerId, entityType: "Country", entityId: country.Id,
            details: new { countryId = country.Id, code }, cancellationToken: cancellationToken);
        return CatalogResult.Success(country);
    }

    public async Task<CatalogResult<Country>> UpdateCountryAsync(int id, SaveCountryCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var country = await store.GetCountryAsync(id, cancellationToken);
        if (country is null) return CatalogResult.Error<Country>(CatalogErrors.NotFound);

        var errors = ValidateCountry(command, creating: false);
        if (errors.Count > 0) return CatalogResult.Failure<Country>(errors);

        Apply(country, command, clock.UtcNow);
        await store.UpdateCountryAsync(country, cancellationToken);

        await auditLog.WriteAsync("country.updated", actorCustomerId, entityType: "Country", entityId: id,
            details: new { countryId = id, code = country.Code, country.Published, country.AllowsBilling, country.AllowsShipping }, cancellationToken: cancellationToken);
        return CatalogResult.Success(country);
    }

    public async Task<CatalogResult<bool>> DeleteCountryAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var country = await store.GetCountryAsync(id, cancellationToken);
        if (country is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);
        // Saved addresses name the country; unpublish it instead of removing it.
        if (await store.CountryInUseAsync(country.Code, cancellationToken)) return CatalogResult.Error<bool>(DirectoryErrors.CountryInUse);

        await store.DeleteCountryAsync(id, cancellationToken);
        await auditLog.WriteAsync("country.deleted", actorCustomerId, entityType: "Country", entityId: id,
            details: new { countryId = id, code = country.Code }, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }

    // ---- Administrators: states ----

    public async Task<CatalogResult<IReadOnlyList<StateProvince>>> GetStatesAsync(int countryId, CancellationToken cancellationToken)
    {
        if (await store.GetCountryAsync(countryId, cancellationToken) is null) return CatalogResult.Error<IReadOnlyList<StateProvince>>(CatalogErrors.NotFound);
        return CatalogResult.Success<IReadOnlyList<StateProvince>>(Ordered(await store.GetStatesAsync(countryId, cancellationToken)));
    }

    public async Task<CatalogResult<StateProvince>> CreateStateAsync(int countryId, SaveStateCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (await store.GetCountryAsync(countryId, cancellationToken) is null) return CatalogResult.Error<StateProvince>(CatalogErrors.NotFound);
        var errors = ValidateState(command);
        if (errors.Count > 0) return CatalogResult.Failure<StateProvince>(errors);

        var code = command.Code!.Trim();
        if (await store.GetStateByCodeAsync(countryId, code, cancellationToken) is not null)
            return CatalogResult.Error<StateProvince>(DirectoryErrors.StateCodeExists);

        var state = new StateProvince { CountryId = countryId, Code = code, Name = command.Name!.Trim(), Published = command.Published, DisplayOrder = command.DisplayOrder };
        state.Id = await store.InsertStateAsync(state, cancellationToken);

        await auditLog.WriteAsync("state.created", actorCustomerId, entityType: "StateProvince", entityId: state.Id,
            details: new { stateId = state.Id, countryId, code }, cancellationToken: cancellationToken);
        return CatalogResult.Success(state);
    }

    public async Task<CatalogResult<StateProvince>> UpdateStateAsync(int id, SaveStateCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var state = await store.GetStateAsync(id, cancellationToken);
        if (state is null) return CatalogResult.Error<StateProvince>(CatalogErrors.NotFound);
        var errors = ValidateState(command);
        if (errors.Count > 0) return CatalogResult.Failure<StateProvince>(errors);

        var code = command.Code!.Trim();
        var same = await store.GetStateByCodeAsync(state.CountryId, code, cancellationToken);
        if (same is not null && same.Id != id) return CatalogResult.Error<StateProvince>(DirectoryErrors.StateCodeExists);

        state.Code = code;
        state.Name = command.Name!.Trim();
        state.Published = command.Published;
        state.DisplayOrder = command.DisplayOrder;
        await store.UpdateStateAsync(state, cancellationToken);

        await auditLog.WriteAsync("state.updated", actorCustomerId, entityType: "StateProvince", entityId: id,
            details: new { stateId = id, state.CountryId, code }, cancellationToken: cancellationToken);
        return CatalogResult.Success(state);
    }

    public async Task<CatalogResult<bool>> DeleteStateAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var state = await store.GetStateAsync(id, cancellationToken);
        if (state is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);
        if (await store.StateInUseAsync(id, cancellationToken)) return CatalogResult.Error<bool>(DirectoryErrors.StateInUse);

        await store.DeleteStateAsync(id, cancellationToken);
        await auditLog.WriteAsync("state.deleted", actorCustomerId, entityType: "StateProvince", entityId: id,
            details: new { stateId = id, state.CountryId, state.Code }, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }

    // ---- Helpers ----

    private static Dictionary<string, string[]> ValidateCountry(SaveCountryCommand command, bool creating)
    {
        var errors = new Dictionary<string, string[]>();
        if (creating)
        {
            var code = command.Code?.Trim() ?? string.Empty;
            if (code.Length != 2 || !code.All(char.IsAsciiLetter)) errors["code"] = ["The code must be two letters, for example VN."];
        }

        var alpha3 = command.Alpha3?.Trim();
        if (!string.IsNullOrEmpty(alpha3) && (alpha3.Length != 3 || !alpha3.All(char.IsAsciiLetter)))
            errors["alpha3"] = ["The three-letter code must be three letters, for example VNM."];

        var name = command.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > CountryLimits.MaxNameLength)
            errors["name"] = [$"The name must be 1 to {CountryLimits.MaxNameLength} characters."];

        var pattern = command.PostalCodePattern?.Trim();
        if (!string.IsNullOrEmpty(pattern))
        {
            if (pattern.Length > CountryLimits.MaxPatternLength) errors["postalCodePattern"] = [$"The pattern cannot exceed {CountryLimits.MaxPatternLength} characters."];
            else if (!PostalCodeRules.IsValidPattern(pattern)) errors["postalCodePattern"] = ["The pattern is not a valid regular expression."];
            else if (!command.PostalCodeRequired) errors["postalCodePattern"] = ["A pattern needs the postal code to be required."];
        }
        return errors;
    }

    private static Dictionary<string, string[]> ValidateState(SaveStateCommand command)
    {
        var errors = new Dictionary<string, string[]>();
        var code = command.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > CountryLimits.MaxStateCodeLength)
            errors["code"] = [$"The code must be 1 to {CountryLimits.MaxStateCodeLength} characters."];
        var name = command.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > CountryLimits.MaxNameLength)
            errors["name"] = [$"The name must be 1 to {CountryLimits.MaxNameLength} characters."];
        return errors;
    }

    private static void Apply(Country country, SaveCountryCommand command, DateTime now)
    {
        country.Alpha3 = string.IsNullOrWhiteSpace(command.Alpha3) ? null : command.Alpha3.Trim().ToUpperInvariant();
        country.Name = command.Name!.Trim();
        country.Published = command.Published;
        country.AllowsBilling = command.AllowsBilling;
        country.AllowsShipping = command.AllowsShipping;
        country.PostalCodeRequired = command.PostalCodeRequired;
        country.PostalCodePattern = string.IsNullOrWhiteSpace(command.PostalCodePattern) ? null : command.PostalCodePattern.Trim();
        country.DisplayOrder = command.DisplayOrder;
        country.UpdatedOnUtc = now;
    }

    private static List<Country> Ordered(IEnumerable<Country> countries) =>
        countries.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();

    private static List<StateProvince> Ordered(IEnumerable<StateProvince> states) =>
        states.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
}
