using System.Text.RegularExpressions;
using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Directory;

public static class CountryLimits
{
    public const int MaxNameLength = 100;
    public const int MaxPatternLength = 200;
    public const int MaxStateCodeLength = 20;
    public const int MaxPostalCodeLength = 20;
    public const int MaxStateTextLength = 100;
}

public enum AddressUse
{
    /// <summary>Saving to the address book: the country may allow billing, shipping or both.</summary>
    Any = 0,
    Billing = 1,
    Shipping = 2
}

public sealed class Country
{
    public int Id { get; set; }

    /// <summary>ISO 3166-1 alpha-2, upper case. Never changes.</summary>
    public string Code { get; set; } = string.Empty;

    public string? Alpha3 { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Customers may pick it for an address.</summary>
    public bool Published { get; set; } = true;

    public bool AllowsBilling { get; set; } = true;
    public bool AllowsShipping { get; set; } = true;
    public bool PostalCodeRequired { get; set; }

    /// <summary>Optional regular expression the whole postal code has to match. Only an administrator writes it.</summary>
    public string? PostalCodePattern { get; set; }

    public int DisplayOrder { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }

    /// <summary>Published states, filled by reads only. When above 0 an address in the country must name one.</summary>
    public int PublishedStateCount { get; set; }

    public bool CanBeUsedFor(AddressUse use) => Published && use switch
    {
        AddressUse.Billing => AllowsBilling,
        AddressUse.Shipping => AllowsShipping,
        _ => AllowsBilling || AllowsShipping
    };
}

public sealed class StateProvince
{
    public int Id { get; set; }
    public int CountryId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Published { get; set; } = true;
    public int DisplayOrder { get; set; }
}

public static class PostalCodeRules
{
    /// <summary>A pattern is trusted from an administrator only, but even then a bad one must not slow the API down.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    public static bool IsValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(Wrap(pattern), RegexOptions.CultureInvariant, Timeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when the whole code matches the pattern. A pattern that is invalid or too slow never matches.</summary>
    public static bool Matches(string pattern, string postalCode)
    {
        try
        {
            return new Regex(Wrap(pattern), RegexOptions.CultureInvariant, Timeout).IsMatch(postalCode);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // The code has to match as a whole, so the pattern is anchored whatever the administrator wrote.
    private static string Wrap(string pattern) => "^(?:" + pattern + ")$";
}

public sealed record SaveCountryCommand(
    string? Code, string? Alpha3, string? Name, bool Published, bool AllowsBilling, bool AllowsShipping,
    bool PostalCodeRequired, string? PostalCodePattern, int DisplayOrder);

public sealed record SaveStateCommand(string? Code, string? Name, bool Published, int DisplayOrder);

/// <summary>The location part of an address after it passed the directory rules.</summary>
public sealed record ResolvedAddressLocation(string CountryCode, int? StateProvinceId, string? StateName, string? PostalCode);

public interface IDirectoryStore
{
    Task<IReadOnlyList<Country>> GetCountriesAsync(CancellationToken cancellationToken);
    Task<Country?> GetCountryAsync(int id, CancellationToken cancellationToken);
    Task<Country?> GetCountryByCodeAsync(string code, CancellationToken cancellationToken);
    Task<int> InsertCountryAsync(Country country, CancellationToken cancellationToken);

    /// <summary>Writes everything except the code.</summary>
    Task UpdateCountryAsync(Country country, CancellationToken cancellationToken);
    Task DeleteCountryAsync(int id, CancellationToken cancellationToken);

    /// <summary>True when a saved address uses the country code.</summary>
    Task<bool> CountryInUseAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<StateProvince>> GetStatesAsync(int countryId, CancellationToken cancellationToken);
    Task<StateProvince?> GetStateAsync(int id, CancellationToken cancellationToken);
    Task<StateProvince?> GetStateByCodeAsync(int countryId, string code, CancellationToken cancellationToken);
    Task<int> InsertStateAsync(StateProvince state, CancellationToken cancellationToken);
    Task UpdateStateAsync(StateProvince state, CancellationToken cancellationToken);
    Task DeleteStateAsync(int id, CancellationToken cancellationToken);

    /// <summary>True when a saved address uses the state.</summary>
    Task<bool> StateInUseAsync(int id, CancellationToken cancellationToken);
}

public interface IDirectoryService
{
    // ---- Public ----

    Task<IReadOnlyList<Country>> GetPublishedCountriesAsync(CancellationToken cancellationToken);

    /// <summary>Published states of a published country; null when the country is unknown or not published.</summary>
    Task<IReadOnlyList<StateProvince>?> GetPublishedStatesAsync(string countryCode, CancellationToken cancellationToken);

    // ---- Address rules ----

    /// <summary>
    /// Checks the country, state and postal code of an address against the directory. Field errors use the address field names
    /// (<c>countryCode</c>, <c>stateProvinceId</c>, <c>zipPostalCode</c>).
    /// </summary>
    Task<CatalogResult<ResolvedAddressLocation>> ResolveAddressAsync(
        string? countryCode, int? stateProvinceId, string? stateText, string? postalCode, AddressUse use, CancellationToken cancellationToken);

    // ---- Administrators ----

    Task<IReadOnlyList<Country>> GetCountriesAsync(CancellationToken cancellationToken);
    Task<CatalogResult<Country>> CreateCountryAsync(SaveCountryCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<Country>> UpdateCountryAsync(int id, SaveCountryCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteCountryAsync(int id, int actorCustomerId, CancellationToken cancellationToken);

    Task<CatalogResult<IReadOnlyList<StateProvince>>> GetStatesAsync(int countryId, CancellationToken cancellationToken);
    Task<CatalogResult<StateProvince>> CreateStateAsync(int countryId, SaveStateCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<StateProvince>> UpdateStateAsync(int id, SaveStateCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteStateAsync(int id, int actorCustomerId, CancellationToken cancellationToken);
}
