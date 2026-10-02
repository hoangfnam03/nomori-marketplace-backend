using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Services.Customers;

namespace Nomori.Marketplace.Services.Tests;

public sealed class AddressBookTests
{
    private sealed class FakeDirectory(Country? country, StateProvince? state = null) : IDirectoryService
    {
        public Task<Nomori.Marketplace.Core.Catalog.CatalogResult<ResolvedAddressLocation>> ResolveAddressAsync(
            string? countryCode, int? stateProvinceId, string? stateText, string? postalCode, AddressUse use, CancellationToken cancellationToken)
        {
            if (country is null || !string.Equals(country.Code, countryCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Nomori.Marketplace.Core.Catalog.CatalogResult.Failure<ResolvedAddressLocation>("countryCode", "We cannot use this country for an address."));
            return Task.FromResult(Nomori.Marketplace.Core.Catalog.CatalogResult.Success(
                new ResolvedAddressLocation(country.Code, state?.Id, state?.Name ?? stateText?.Trim(), postalCode?.Trim())));
        }

        public Task<IReadOnlyList<Country>> GetPublishedCountriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<StateProvince>?> GetPublishedStatesAsync(string countryCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Country>> GetCountriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Nomori.Marketplace.Core.Catalog.CatalogResult<Country>> CreateCountryAsync(SaveCountryCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Nomori.Marketplace.Core.Catalog.CatalogResult<Country>> UpdateCountryAsync(int id, SaveCountryCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Nomori.Marketplace.Core.Catalog.CatalogResult<bool>> DeleteCountryAsync(int id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Nomori.Marketplace.Core.Catalog.CatalogResult<IReadOnlyList<StateProvince>>> GetStatesAsync(int countryId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Nomori.Marketplace.Core.Catalog.CatalogResult<StateProvince>> CreateStateAsync(int countryId, SaveStateCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Nomori.Marketplace.Core.Catalog.CatalogResult<StateProvince>> UpdateStateAsync(int id, SaveStateCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Nomori.Marketplace.Core.Catalog.CatalogResult<bool>> DeleteStateAsync(int id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeAccountStore : ICustomerAccountDataStore
    {
        public CustomerAddress? Saved { get; private set; }

        public Task<int> SaveAddressAsync(CustomerAddress address, CancellationToken cancellationToken)
        {
            Saved = address;
            return Task.FromResult(address.Id == 0 ? 42 : address.Id);
        }

        public Task<IReadOnlyList<CustomerAddress>> GetAddressesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CustomerAddress?> GetAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CustomerAttributeSet> GetAttributesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAttributesAsync(int customerId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CreateEmailChangeAsync(int customerId, string newEmail, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(int CustomerId, string NewEmail)?> ConsumeEmailChangeAsync(string tokenHash, DateTime nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ApplyEmailChangeAsync(int customerId, string newEmail, DateTime nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static (CustomerAccountDataService Service, FakeAccountStore Store, RecordingAuditLog Audit) Create(Country? country, StateProvince? state = null)
    {
        var store = new FakeAccountStore();
        var audit = new RecordingAuditLog();
        // Only the address path is exercised; the password, email and settings dependencies are not used by it.
        var service = new CustomerAccountDataService(store, new FakeDirectory(country, state), null!, null!, null!, audit, new TestClock(), null!, null!);
        return (service, store, audit);
    }

    private static CustomerAddress Valid(string country = "US") => new()
    {
        CustomerId = 7, FirstName = " Ann ", LastName = "Lee", Address1 = "1 Main St", City = "Springfield", CountryCode = country,
        PhoneNumber = "+1 555 0100", ZipPostalCode = " 94105 ", StateProvince = "typed text"
    };

    [Fact]
    public async Task ASavedAddressTakesItsLocationFromTheDirectory()
    {
        var us = new Country { Id = 1, Code = "US", Name = "United States" };
        var ny = new StateProvince { Id = 9, CountryId = 1, Code = "NY", Name = "New York" };
        var (service, store, audit) = Create(us, ny);

        var (address, errors) = await service.SaveAddressAsync(Valid("us"), CancellationToken.None);

        Assert.Empty(errors);
        Assert.Equal(("US", 9, "New York", "94105"), (address!.CountryCode, address.StateProvinceId, address.StateProvince, address.ZipPostalCode));
        Assert.Equal("Ann", store.Saved!.FirstName);
        Assert.Contains(audit.Entries, e => e.Event == "customer.address_created");
    }

    [Fact]
    public async Task ACountryTheDirectoryRefusesStopsTheSave()
    {
        var (service, store, _) = Create(new Country { Code = "US", Name = "United States" });

        var (address, errors) = await service.SaveAddressAsync(Valid("ZZ"), CancellationToken.None);

        Assert.Null(address);
        Assert.Contains("countryCode", errors.Keys);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task OtherAddressRulesStillApplyAlongsideTheDirectory()
    {
        var (service, store, _) = Create(new Country { Code = "US", Name = "United States" });
        var bad = Valid();
        bad.FirstName = " ";
        bad.PhoneNumber = "call me";

        var (address, errors) = await service.SaveAddressAsync(bad, CancellationToken.None);

        Assert.Null(address);
        Assert.Contains("firstName", errors.Keys);
        Assert.Contains("phoneNumber", errors.Keys);
        Assert.Null(store.Saved);
    }
}
