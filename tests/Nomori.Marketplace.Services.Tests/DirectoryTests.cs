using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Services.Directory;

namespace Nomori.Marketplace.Services.Tests;

public sealed class DirectoryTests
{
    private const int Admin = 1;

    internal sealed class FakeDirectoryStore : IDirectoryStore
    {
        private int nextCountryId = 1;
        private int nextStateId = 1;

        public List<Country> Countries { get; } = [];
        public List<StateProvince> States { get; } = [];
        public HashSet<string> CodesInUse { get; } = [];
        public HashSet<int> StatesInUse { get; } = [];

        private Country WithCount(Country c)
        {
            c.PublishedStateCount = States.Count(s => s.CountryId == c.Id && s.Published);
            return c;
        }

        public Task<IReadOnlyList<Country>> GetCountriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Country>>(Countries.Select(WithCount).ToList());

        public Task<Country?> GetCountryAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(Countries.Where(c => c.Id == id).Select(WithCount).FirstOrDefault());

        public Task<Country?> GetCountryByCodeAsync(string code, CancellationToken cancellationToken) =>
            Task.FromResult(Countries.Where(c => c.Code == code).Select(WithCount).FirstOrDefault());

        public Task<int> InsertCountryAsync(Country country, CancellationToken cancellationToken)
        {
            country.Id = nextCountryId++;
            Countries.Add(country);
            return Task.FromResult(country.Id);
        }

        public Task UpdateCountryAsync(Country country, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteCountryAsync(int id, CancellationToken cancellationToken)
        {
            Countries.RemoveAll(c => c.Id == id);
            States.RemoveAll(s => s.CountryId == id);
            return Task.CompletedTask;
        }

        public Task<bool> CountryInUseAsync(string code, CancellationToken cancellationToken) => Task.FromResult(CodesInUse.Contains(code));

        public Task<IReadOnlyList<StateProvince>> GetStatesAsync(int countryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StateProvince>>(States.Where(s => s.CountryId == countryId).ToList());

        public Task<StateProvince?> GetStateAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(States.FirstOrDefault(s => s.Id == id));

        public Task<StateProvince?> GetStateByCodeAsync(int countryId, string code, CancellationToken cancellationToken) =>
            Task.FromResult(States.FirstOrDefault(s => s.CountryId == countryId && string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)));

        public Task<int> InsertStateAsync(StateProvince state, CancellationToken cancellationToken)
        {
            state.Id = nextStateId++;
            States.Add(state);
            return Task.FromResult(state.Id);
        }

        public Task UpdateStateAsync(StateProvince state, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteStateAsync(int id, CancellationToken cancellationToken)
        {
            States.RemoveAll(s => s.Id == id);
            return Task.CompletedTask;
        }

        public Task<bool> StateInUseAsync(int id, CancellationToken cancellationToken) => Task.FromResult(StatesInUse.Contains(id));
    }

    private sealed class Fixture
    {
        public FakeDirectoryStore Store { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public DirectoryService Create() => new(Store, Audit, new TestClock());

        public async Task<Country> CountryAsync(string code, string name = "Country", Action<SaveCountryCommand>? _ = null, bool zipRequired = false, string? pattern = null, bool published = true, bool billing = true, bool shipping = true)
        {
            var result = await Create().CreateCountryAsync(
                new SaveCountryCommand(code, null, name, published, billing, shipping, zipRequired, pattern, 0), Admin, CancellationToken.None);
            return result.Value!;
        }

        public async Task<StateProvince> StateAsync(Country country, string code, string name, bool published = true)
        {
            var result = await Create().CreateStateAsync(country.Id, new SaveStateCommand(code, name, published, 0), Admin, CancellationToken.None);
            return result.Value!;
        }
    }

    private static Task<CatalogResult<ResolvedAddressLocation>> Resolve(
        Fixture f, string? country, int? stateId = null, string? stateText = null, string? zip = null, AddressUse use = AddressUse.Any) =>
        f.Create().ResolveAddressAsync(country, stateId, stateText, zip, use, CancellationToken.None);

    // ---- Postal code rules ----

    [Theory]
    [InlineData(@"\d{5}(-\d{4})?", "94105", true)]
    [InlineData(@"\d{5}(-\d{4})?", "94105-1234", true)]
    [InlineData(@"\d{5}(-\d{4})?", "9410", false)]
    [InlineData(@"\d{5}(-\d{4})?", "94105 ", false)]
    [InlineData(@"\d{5}(-\d{4})?", "ab105", false)]
    [InlineData(@"[A-Za-z]{1,2}\d[A-Za-z\d]? ?\d[A-Za-z]{2}", "SW1A 1AA", true)]
    [InlineData(@"[A-Za-z]{1,2}\d[A-Za-z\d]? ?\d[A-Za-z]{2}", "M1 1AE", true)]
    [InlineData(@"\d{3}-?\d{4}", "100-0001", true)]
    [InlineData(@"\d{3}-?\d{4}", "1000001", true)]
    [InlineData(@"\d{4} ?[A-Za-z]{2}", "1011 AB", true)]
    public void PatternsMatchTheWholeCode(string pattern, string code, bool expected) =>
        Assert.Equal(expected, PostalCodeRules.Matches(pattern, code));

    [Fact]
    public void ThePatternIsAnchoredEvenWhenTheAdministratorForgotTo()
    {
        Assert.False(PostalCodeRules.Matches(@"\d{5}", "123456"));
        Assert.False(PostalCodeRules.Matches(@"\d{5}", "x12345"));
        // An administrator may anchor it too.
        Assert.True(PostalCodeRules.Matches(@"^\d{5}$", "12345"));
    }

    [Theory]
    [InlineData(@"\d{5}", true)]
    [InlineData(@"(abc", false)]
    [InlineData(@"[a-", false)]
    [InlineData(@"*", false)]
    public void PatternsAreCheckedForSyntax(string pattern, bool valid) =>
        Assert.Equal(valid, PostalCodeRules.IsValidPattern(pattern));

    [Fact]
    public void ABrokenOrRunawayPatternNeverMatchesAndNeverThrows()
    {
        Assert.False(PostalCodeRules.Matches("(abc", "abc"));
        // Catastrophic backtracking: stopped by the time limit instead of hanging the request.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(PostalCodeRules.Matches(@"(a+)+$", new string('a', 60) + "!"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    // ---- Countries ----

    [Fact]
    public async Task ACountryIsCreatedWithAnUpperCaseCode()
    {
        var f = new Fixture();

        var result = await f.Create().CreateCountryAsync(new SaveCountryCommand("vn", "vnm", " Vietnam ", true, true, true, false, null, 1), Admin, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(("VN", "VNM", "Vietnam"), (result.Value!.Code, result.Value.Alpha3, result.Value.Name));
        Assert.Contains(f.Audit.Entries, e => e.Event == "country.created");
    }

    [Theory]
    [InlineData("V")]
    [InlineData("VNM")]
    [InlineData("V1")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ACountryCodeIsTwoLetters(string? code)
    {
        var result = await new Fixture().Create().CreateCountryAsync(new SaveCountryCommand(code, null, "X", true, true, true, false, null, 0), Admin, CancellationToken.None);

        Assert.Contains("code", result.Errors.Keys);
    }

    [Fact]
    public async Task CountryFieldsAreValidated()
    {
        var service = new Fixture().Create();

        Task<CatalogResult<Country>> Try(SaveCountryCommand c) => service.CreateCountryAsync(c, Admin, CancellationToken.None);
        SaveCountryCommand Make(string? alpha3 = null, string? name = "Name", bool zip = true, string? pattern = null) =>
            new("AA", alpha3, name, true, true, true, zip, pattern, 0);

        Assert.Contains("alpha3", (await Try(Make(alpha3: "AB"))).Errors.Keys);
        Assert.Contains("name", (await Try(Make(name: " "))).Errors.Keys);
        Assert.Contains("name", (await Try(Make(name: new string('n', 101)))).Errors.Keys);
        Assert.Contains("postalCodePattern", (await Try(Make(pattern: "(abc"))).Errors.Keys);
        Assert.Contains("postalCodePattern", (await Try(Make(pattern: new string('a', 201)))).Errors.Keys);
        Assert.Contains("postalCodePattern", (await Try(Make(zip: false, pattern: @"\d{5}"))).Errors.Keys);
        Assert.True((await Try(Make(pattern: @"\d{5}"))).Succeeded);
    }

    [Fact]
    public async Task ACountryCodeIsUniqueIgnoringCase()
    {
        var f = new Fixture();
        await f.CountryAsync("VN");

        var result = await f.Create().CreateCountryAsync(new SaveCountryCommand("vn", null, "Other", true, true, true, false, null, 0), Admin, CancellationToken.None);

        Assert.Equal(DirectoryErrors.CountryCodeExists, result.ErrorCode);
    }

    [Fact]
    public async Task UpdatingNeverChangesTheCode()
    {
        var f = new Fixture();
        var country = await f.CountryAsync("VN", "Vietnam");

        var updated = await f.Create().UpdateCountryAsync(country.Id, new SaveCountryCommand("XX", null, "Viet Nam", false, true, false, true, @"\d{6}", 4), Admin, CancellationToken.None);

        Assert.True(updated.Succeeded);
        Assert.Equal(("VN", "Viet Nam", false, false), (updated.Value!.Code, updated.Value.Name, updated.Value.Published, updated.Value.AllowsShipping));
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().UpdateCountryAsync(999, new SaveCountryCommand(null, null, "X", true, true, true, false, null, 0), Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ACountryUsedByAnAddressCannotBeDeleted()
    {
        var f = new Fixture();
        var used = await f.CountryAsync("VN");
        var free = await f.CountryAsync("US");
        f.Store.CodesInUse.Add("VN");

        Assert.Equal(DirectoryErrors.CountryInUse, (await f.Create().DeleteCountryAsync(used.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.True((await f.Create().DeleteCountryAsync(free.Id, Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().DeleteCountryAsync(free.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Contains(f.Audit.Entries, e => e.Event == "country.deleted");
    }

    [Fact]
    public async Task OnlyPublishedCountriesAreListedPubliclyInOrder()
    {
        var f = new Fixture();
        var service = f.Create();
        await service.CreateCountryAsync(new SaveCountryCommand("US", null, "United States", true, true, true, false, null, 2), Admin, CancellationToken.None);
        await service.CreateCountryAsync(new SaveCountryCommand("VN", null, "Vietnam", true, true, true, false, null, 1), Admin, CancellationToken.None);
        await service.CreateCountryAsync(new SaveCountryCommand("ZZ", null, "Hidden", false, true, true, false, null, 0), Admin, CancellationToken.None);

        Assert.Equal(["VN", "US"], (await service.GetPublishedCountriesAsync(CancellationToken.None)).Select(c => c.Code));
        Assert.Equal(3, (await service.GetCountriesAsync(CancellationToken.None)).Count);
    }

    // ---- States ----

    [Fact]
    public async Task StatesAreValidatedAndUniquePerCountry()
    {
        var f = new Fixture();
        var us = await f.CountryAsync("US");
        var ca = await f.CountryAsync("CA");
        await f.StateAsync(us, "CA", "California");
        var service = f.Create();

        Task<CatalogResult<StateProvince>> Try(int countryId, string? code, string? name) =>
            service.CreateStateAsync(countryId, new SaveStateCommand(code, name, true, 0), Admin, CancellationToken.None);

        Assert.Contains("code", (await Try(us.Id, "", "X")).Errors.Keys);
        Assert.Contains("code", (await Try(us.Id, new string('c', 21), "X")).Errors.Keys);
        Assert.Contains("name", (await Try(us.Id, "NY", " ")).Errors.Keys);
        Assert.Equal(DirectoryErrors.StateCodeExists, (await Try(us.Id, "ca", "Again")).ErrorCode);
        // The same code is fine in another country.
        Assert.True((await Try(ca.Id, "CA", "Canada state")).Succeeded);
        Assert.Equal(CatalogErrors.NotFound, (await Try(999, "NY", "New York")).ErrorCode);
    }

    [Fact]
    public async Task AStateCodeCanBeKeptOnUpdateButNotStolen()
    {
        var f = new Fixture();
        var us = await f.CountryAsync("US");
        var ca = await f.StateAsync(us, "CA", "California");
        var ny = await f.StateAsync(us, "NY", "New York");
        var service = f.Create();

        Assert.True((await service.UpdateStateAsync(ca.Id, new SaveStateCommand("CA", "California!", true, 1), Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(DirectoryErrors.StateCodeExists, (await service.UpdateStateAsync(ny.Id, new SaveStateCommand("ca", "New York", true, 1), Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.UpdateStateAsync(999, new SaveStateCommand("X", "X", true, 0), Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task AStateUsedByAnAddressCannotBeDeleted()
    {
        var f = new Fixture();
        var us = await f.CountryAsync("US");
        var used = await f.StateAsync(us, "CA", "California");
        var free = await f.StateAsync(us, "NY", "New York");
        f.Store.StatesInUse.Add(used.Id);

        Assert.Equal(DirectoryErrors.StateInUse, (await f.Create().DeleteStateAsync(used.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.True((await f.Create().DeleteStateAsync(free.Id, Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().DeleteStateAsync(free.Id, Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task OnlyPublishedStatesOfPublishedCountriesAreListedPublicly()
    {
        var f = new Fixture();
        var us = await f.CountryAsync("US");
        var hidden = await f.CountryAsync("ZZ", published: false);
        await f.StateAsync(us, "NY", "New York");
        await f.StateAsync(us, "CA", "California");
        await f.StateAsync(us, "TX", "Texas", published: false);
        await f.StateAsync(hidden, "AA", "Hidden state");
        var service = f.Create();

        Assert.Equal(["California", "New York"], (await service.GetPublishedStatesAsync("us", CancellationToken.None))!.Select(s => s.Name));
        Assert.Null(await service.GetPublishedStatesAsync("ZZ", CancellationToken.None));
        Assert.Null(await service.GetPublishedStatesAsync("QQ", CancellationToken.None));
        Assert.Null(await service.GetPublishedStatesAsync("", CancellationToken.None));
        Assert.Equal(2, (await service.GetPublishedCountriesAsync(CancellationToken.None)).Single(c => c.Code == "US").PublishedStateCount);
    }

    // ---- Address rules ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("V")]
    [InlineData("V1")]
    [InlineData("VNM")]
    public async Task ACountryCodeOfTheWrongShapeIsRefused(string? code)
    {
        var f = new Fixture();
        await f.CountryAsync("VN");

        Assert.Contains("countryCode", (await Resolve(f, code)).Errors.Keys);
    }

    [Fact]
    public async Task AnUnknownOrUnpublishedCountryCannotBeUsed()
    {
        var f = new Fixture();
        await f.CountryAsync("VN");
        await f.CountryAsync("ZZ", published: false);
        await f.CountryAsync("NN", billing: false, shipping: false);

        Assert.Contains("countryCode", (await Resolve(f, "XX")).Errors.Keys);
        Assert.Contains("countryCode", (await Resolve(f, "ZZ")).Errors.Keys);
        Assert.Contains("countryCode", (await Resolve(f, "NN")).Errors.Keys);
        Assert.True((await Resolve(f, "vn")).Succeeded);
        Assert.Equal("VN", (await Resolve(f, " vn ")).Value!.CountryCode);
    }

    [Fact]
    public async Task BillingAndShippingUsesFollowTheFlags()
    {
        var f = new Fixture();
        await f.CountryAsync("AA", shipping: false);
        await f.CountryAsync("BB", billing: false);

        Assert.True((await Resolve(f, "AA", use: AddressUse.Billing)).Succeeded);
        Assert.Contains("countryCode", (await Resolve(f, "AA", use: AddressUse.Shipping)).Errors.Keys);
        Assert.True((await Resolve(f, "BB", use: AddressUse.Shipping)).Succeeded);
        Assert.Contains("countryCode", (await Resolve(f, "BB", use: AddressUse.Billing)).Errors.Keys);
        // The address book accepts a country that allows either.
        Assert.True((await Resolve(f, "AA")).Succeeded);
        Assert.True((await Resolve(f, "BB")).Succeeded);
    }

    [Fact]
    public async Task ACountryWithStatesNeedsOneOfThem()
    {
        var f = new Fixture();
        var us = await f.CountryAsync("US");
        var other = await f.CountryAsync("CA");
        var ny = await f.StateAsync(us, "NY", "New York");
        var foreign = await f.StateAsync(other, "ON", "Ontario");
        var unpublished = await f.StateAsync(us, "TX", "Texas", published: false);

        Assert.Contains("stateProvinceId", (await Resolve(f, "US")).Errors.Keys);
        Assert.Contains("stateProvinceId", (await Resolve(f, "US", 999)).Errors.Keys);
        Assert.Contains("stateProvinceId", (await Resolve(f, "US", foreign.Id)).Errors.Keys);
        Assert.Contains("stateProvinceId", (await Resolve(f, "US", unpublished.Id)).Errors.Keys);

        // Free text is ignored when the country has states: the stored name comes from the directory.
        var ok = await Resolve(f, "US", ny.Id, "whatever");
        Assert.Equal((ny.Id, "New York"), (ok.Value!.StateProvinceId, ok.Value.StateName));
    }

    [Fact]
    public async Task ACountryWithoutStatesKeepsTheFreeText()
    {
        var f = new Fixture();
        await f.CountryAsync("VN");

        var result = await Resolve(f, "VN", null, "  Ha Noi ");
        Assert.Equal((null, "Ha Noi"), (result.Value!.StateProvinceId, result.Value.StateName));
        Assert.Null((await Resolve(f, "VN", null, "   ")).Value!.StateName);
        Assert.Contains("stateProvinceId", (await Resolve(f, "VN", 5)).Errors.Keys);
        Assert.Contains("stateProvince", (await Resolve(f, "VN", null, new string('s', 101))).Errors.Keys);
    }

    [Fact]
    public async Task ThePostalCodeFollowsTheCountry()
    {
        var f = new Fixture();
        await f.CountryAsync("US", zipRequired: true, pattern: @"\d{5}(-\d{4})?");
        await f.CountryAsync("VN");
        await f.CountryAsync("DE", zipRequired: true);

        Assert.Contains("zipPostalCode", (await Resolve(f, "US")).Errors.Keys);
        Assert.Contains("zipPostalCode", (await Resolve(f, "US", zip: "  ")).Errors.Keys);
        Assert.Contains("zipPostalCode", (await Resolve(f, "US", zip: "1234")).Errors.Keys);
        Assert.Equal("94105", (await Resolve(f, "US", zip: " 94105 ")).Value!.PostalCode);
        // Optional and free in a country without rules.
        Assert.Null((await Resolve(f, "VN")).Value!.PostalCode);
        Assert.Equal("abc", (await Resolve(f, "VN", zip: "abc")).Value!.PostalCode);
        // Required without a pattern: any value is fine.
        Assert.True((await Resolve(f, "DE", zip: "x")).Succeeded);
        Assert.Contains("zipPostalCode", (await Resolve(f, "VN", zip: new string('9', 21))).Errors.Keys);
    }

    [Fact]
    public async Task SeveralLocationProblemsAreReportedTogether()
    {
        var f = new Fixture();
        var us = await f.CountryAsync("US", zipRequired: true, pattern: @"\d{5}");
        await f.StateAsync(us, "NY", "New York");

        var result = await Resolve(f, "US", null, null, "abc");

        Assert.Contains("stateProvinceId", result.Errors.Keys);
        Assert.Contains("zipPostalCode", result.Errors.Keys);
    }

    [Fact]
    public async Task ACountryWithABadStoredPatternRefusesPostalCodesInsteadOfFailing()
    {
        var f = new Fixture();
        var country = await f.CountryAsync("XX", zipRequired: true);
        country.PostalCodePattern = "(broken";

        Assert.Contains("zipPostalCode", (await Resolve(f, "XX", zip: "123")).Errors.Keys);
    }

    [Fact]
    public async Task AuditHoldsIdsAndCodesOnly()
    {
        var f = new Fixture();
        var country = await f.CountryAsync("VN", "Secret country name");
        await f.StateAsync(country, "HN", "Secret state name");
        await f.Create().UpdateCountryAsync(country.Id, new SaveCountryCommand(null, null, "Secret country name 2", true, true, true, false, null, 0), Admin, CancellationToken.None);

        foreach (var entry in f.Audit.Entries.Where(e => e.Event.StartsWith("country.", StringComparison.Ordinal) || e.Event.StartsWith("state.", StringComparison.Ordinal)))
            Assert.DoesNotContain("Secret", entry.Details?.ToString() ?? string.Empty);
    }
}
