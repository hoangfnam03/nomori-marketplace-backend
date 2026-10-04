using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Tax;
using Nomori.Marketplace.Services.Directory;
using Nomori.Marketplace.Services.Tax;
using static Nomori.Marketplace.Services.Tests.DirectoryTests;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class TaxTests
{
    private const int Admin = 1;
    private const int Shop = 5;
    private const int OtherShop = 6;

    /// <summary>In-memory tax data with the same rules as the database: unique names, one rate per place, one default.</summary>
    internal sealed class FakeTaxStore : ITaxStore
    {
        private int nextCategoryId = 2;
        private int nextRateId = 1;

        public List<TaxCategory> Categories { get; } = [new TaxCategory { Id = 1, Name = "Standard", IsDefault = true }];
        public List<TaxRate> Rates { get; } = [];
        public Dictionary<int, int> Assignments { get; } = [];

        public Task<IReadOnlyList<TaxCategory>> GetCategoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaxCategory>>(Categories.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToList());

        public Task<TaxCategory?> GetCategoryAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Categories.FirstOrDefault(c => c.Id == id));

        public Task<int> InsertCategoryAsync(TaxCategory category, CancellationToken cancellationToken)
        {
            if (Categories.Any(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase))) return Task.FromResult(0);
            category.Id = nextCategoryId++;
            Categories.Add(category);
            return Task.FromResult(category.Id);
        }

        public Task<bool> UpdateCategoryAsync(TaxCategory category, CancellationToken cancellationToken) =>
            Task.FromResult(!Categories.Any(c => c.Id != category.Id && string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase)));

        public Task DeleteCategoryAsync(int id, CancellationToken cancellationToken)
        {
            Categories.RemoveAll(c => c.Id == id);
            return Task.CompletedTask;
        }

        public Task<int> CountRatesAsync(int categoryId, CancellationToken cancellationToken) => Task.FromResult(Rates.Count(r => r.CategoryId == categoryId));
        public Task<int> CountProductsAsync(int categoryId, CancellationToken cancellationToken) => Task.FromResult(Assignments.Values.Count(v => v == categoryId));

        public Task<IReadOnlyList<TaxRate>> GetRatesAsync(int categoryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaxRate>>(Rates.Where(r => r.CategoryId == categoryId).ToList());

        public Task<TaxRate?> GetRateAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Rates.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<TaxRate>> GetPublishedRatesAsync(string countryCode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaxRate>>(Rates.Where(r => r.Published && r.CountryCode == countryCode).ToList());

        public Task<int> InsertRateAsync(TaxRate rate, CancellationToken cancellationToken)
        {
            if (Rates.Any(r => r.CategoryId == rate.CategoryId && r.CountryCode == rate.CountryCode && r.StateProvinceId == rate.StateProvinceId)) return Task.FromResult(0);
            rate.Id = nextRateId++;
            Rates.Add(rate);
            return Task.FromResult(rate.Id);
        }

        public Task<bool> UpdateRateAsync(TaxRate rate, CancellationToken cancellationToken) =>
            Task.FromResult(!Rates.Any(r => r.Id != rate.Id && r.CategoryId == rate.CategoryId && r.CountryCode == rate.CountryCode && r.StateProvinceId == rate.StateProvinceId));

        public Task DeleteRateAsync(int id, CancellationToken cancellationToken)
        {
            Rates.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<int, int>> GetProductCategoryIdsAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(Assignments.Where(a => productIds.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value));

        public Task SetProductCategoryAsync(int productId, int? categoryId, CancellationToken cancellationToken)
        {
            if (categoryId is { } id) Assignments[productId] = id;
            else Assignments.Remove(productId);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public FakeTaxStore Store { get; } = new();
        public FakeProductStore Products { get; } = new();
        public FakeDirectoryStore Directory { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture()
        {
            Directory.Countries.Add(new Country { Id = 1, Code = "US", Name = "United States", Published = true, AllowsBilling = true, AllowsShipping = true });
            Directory.Countries.Add(new Country { Id = 2, Code = "FR", Name = "France", Published = true, AllowsBilling = true, AllowsShipping = true });
            Directory.Countries.Add(new Country { Id = 3, Code = "XX", Name = "Hidden", Published = false });
            Directory.States.Add(new StateProvince { Id = 1, CountryId = 1, Code = "CA", Name = "California", Published = true });
            Directory.States.Add(new StateProvince { Id = 2, CountryId = 1, Code = "NY", Name = "New York", Published = true });
            Products.Products.Add(new Product { Id = 1, Name = "Mug", VendorId = Shop });
            Products.Products.Add(new Product { Id = 2, Name = "Tea", VendorId = OtherShop });
        }

        public TaxService Create(int decimals = 2) => new(
            Store, Products, new DirectoryService(Directory, Audit, new TestClock()), new FakePrimaryCurrency(decimals), Audit, new TestClock());
    }

    private static TaxRate Rate(int category = 1, string country = "US", int? state = null, decimal percentage = 10, bool published = true, int id = 0) =>
        new() { Id = id, CategoryId = category, CountryCode = country, StateProvinceId = state, Percentage = percentage, Published = published };

    private static readonly IReadOnlyDictionary<int, decimal> NoDiscount = new Dictionary<int, decimal>();

    // ---- Pure rules: finding the rate ----

    [Fact]
    public void TheStateRateBeatsTheCountryRateAndNoRateIsZero()
    {
        TaxRate[] rates = [Rate(percentage: 7), Rate(state: 1, percentage: 9.5m)];

        Assert.Equal(9.5m, TaxRules.FindRate(rates, 1, "US", 1));
        Assert.Equal(7m, TaxRules.FindRate(rates, 1, "US", 2));
        Assert.Equal(7m, TaxRules.FindRate(rates, 1, "us", null));
        Assert.Equal(0m, TaxRules.FindRate(rates, 1, "FR", null));
        Assert.Equal(0m, TaxRules.FindRate(rates, 2, "US", 1));
    }

    [Fact]
    public void UnpublishedRatesAreIgnoredAndAStateRateAloneDoesNotCoverTheCountry()
    {
        Assert.Equal(0m, TaxRules.FindRate([Rate(published: false)], 1, "US", null));
        Assert.Equal(7m, TaxRules.FindRate([Rate(percentage: 7), Rate(state: 1, percentage: 9, published: false)], 1, "US", 1));
        Assert.Equal(0m, TaxRules.FindRate([Rate(state: 1, percentage: 9)], 1, "US", 2));
        Assert.Equal(0m, TaxRules.FindRate([Rate(state: 1, percentage: 9)], 1, "US", null));
    }

    // ---- Pure rules: calculating ----

    [Fact]
    public void TaxIsTheRateOfTheLineRoundedToTheCurrency()
    {
        var result = TaxRules.Calculate(
            [new TaxableLine(Shop, 1, 20m), new TaxableLine(Shop, 2, 0.5m)], NoDiscount, p => p == 1 ? 10m : 5m, 2);

        Assert.Equal([2m, 0.03m], result.Lines.Select(l => l.Tax));
        Assert.Equal([10m, 5m], result.Lines.Select(l => l.Rate));
        Assert.Equal(2.03m, result.Total);
        Assert.Equal(2.03m, result.ShopTax[Shop]);
    }

    [Fact]
    public void RoundingFollowsTheDecimalsOfTheCurrency()
    {
        var zeroDecimals = TaxRules.Calculate([new TaxableLine(Shop, 1, 15m)], NoDiscount, _ => 10m, 0);

        Assert.Equal(2m, zeroDecimals.Total);
    }

    [Fact]
    public void TheDiscountOfAShopLowersTheBaseOfItsLinesAndTaxIsNeverChargedOnIt()
    {
        var lines = new[] { new TaxableLine(Shop, 1, 20m), new TaxableLine(Shop, 2, 30m) };

        var result = TaxRules.Calculate(lines, new Dictionary<int, decimal> { [Shop] = 10m }, _ => 10m, 2);

        // The discount is spread 4 and 6; the bases are 16 and 24.
        Assert.Equal([16m, 24m], result.Lines.Select(l => l.Base));
        Assert.Equal([1.6m, 2.4m], result.Lines.Select(l => l.Tax));
        Assert.Equal(4m, result.Total);
    }

    [Fact]
    public void TheDiscountOfOneShopDoesNotTouchTheLinesOfAnother()
    {
        var lines = new[] { new TaxableLine(Shop, 1, 20m), new TaxableLine(OtherShop, 2, 20m) };

        var result = TaxRules.Calculate(lines, new Dictionary<int, decimal> { [Shop] = 20m }, _ => 10m, 2);

        Assert.Equal((0m, 2m), (result.ShopTax[Shop], result.ShopTax[OtherShop]));
        Assert.Equal([0m, 20m], result.Lines.Select(l => l.Base));
    }

    [Fact]
    public void LinesKeepTheOrderTheyWereGivenAndEveryShopHasATotal()
    {
        var lines = new[] { new TaxableLine(OtherShop, 2, 10m), new TaxableLine(Shop, 1, 10m), new TaxableLine(OtherShop, 3, 10m) };

        var result = TaxRules.Calculate(lines, NoDiscount, p => p, 2);

        Assert.Equal([2, 1, 3], result.Lines.Select(l => l.ProductId));
        Assert.Equal((0.5m, 0.1m), (result.ShopTax[OtherShop], result.ShopTax[Shop]));
        Assert.Equal(0.6m, result.Total);
    }

    [Fact]
    public void ADiscountOfTheWholeShopLeavesNothingToTax()
    {
        var result = TaxRules.Calculate([new TaxableLine(Shop, 1, 20m)], new Dictionary<int, decimal> { [Shop] = 20m }, _ => 10m, 2);

        Assert.Equal(0m, result.Total);
        Assert.Equal(0m, result.Lines[0].Base);
    }

    // ---- Service: categories ----

    [Fact]
    public async Task ACategoryIsCreatedRenamedAndAudited()
    {
        var f = new Fixture();

        var created = await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand(" Reduced ", 2), Admin, CancellationToken.None);
        var renamed = await f.Create().UpdateCategoryAsync(created.Value!.Id, new SaveTaxCategoryCommand("Books", 3), Admin, CancellationToken.None);

        Assert.True(created.Succeeded);
        Assert.Equal(("Books", 3, false), (renamed.Value!.Name, renamed.Value.DisplayOrder, renamed.Value.IsDefault));
        Assert.Equal(["tax.category_created", "tax.category_updated"], f.Audit.Events);
    }

    [Fact]
    public async Task CategoryNamesAreCheckedAndUnique()
    {
        var f = new Fixture();

        Assert.Contains("name", (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand(" ", 0), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("name", (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand(new string('x', 101), 0), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Equal(TaxErrors.CategoryExists, (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("standard", 0), Admin, CancellationToken.None)).ErrorCode);

        var other = (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("Reduced", 0), Admin, CancellationToken.None)).Value!;
        Assert.Equal(TaxErrors.CategoryExists, (await f.Create().UpdateCategoryAsync(other.Id, new SaveTaxCategoryCommand("STANDARD", 0), Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().UpdateCategoryAsync(99, new SaveTaxCategoryCommand("x", 0), Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ThereAreAtMostTwentyCategories()
    {
        var f = new Fixture();
        for (var i = 2; i <= TaxLimits.MaxCategories; i++) f.Store.Categories.Add(new TaxCategory { Id = i, Name = "C" + i });

        var result = await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("One more", 0), Admin, CancellationToken.None);

        Assert.Equal(TaxErrors.CategoryLimit, result.ErrorCode);
    }

    [Fact]
    public async Task TheDefaultCategoryAndACategoryInUseCannotBeDeleted()
    {
        var f = new Fixture();
        var withRate = (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("WithRate", 0), Admin, CancellationToken.None)).Value!;
        await f.Create().CreateRateAsync(withRate.Id, new SaveTaxRateCommand("US", null, 5, true), Admin, CancellationToken.None);
        var withProduct = (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("WithProduct", 0), Admin, CancellationToken.None)).Value!;
        f.Store.Assignments[1] = withProduct.Id;
        var unused = (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("Unused", 0), Admin, CancellationToken.None)).Value!;

        Assert.Equal(TaxErrors.DefaultCategory, (await f.Create().DeleteCategoryAsync(1, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(TaxErrors.CategoryInUse, (await f.Create().DeleteCategoryAsync(withRate.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(TaxErrors.CategoryInUse, (await f.Create().DeleteCategoryAsync(withProduct.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.True((await f.Create().DeleteCategoryAsync(unused.Id, Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().DeleteCategoryAsync(99, Admin, CancellationToken.None)).ErrorCode);
        Assert.Single(f.Audit.Events, e => e == "tax.category_deleted");
    }

    // ---- Service: rates ----

    [Fact]
    public async Task ARateIsCreatedForACountryOrAStateAndAudited()
    {
        var f = new Fixture();

        var country = await f.Create().CreateRateAsync(1, new SaveTaxRateCommand(" us ", null, 7.25m, true), Admin, CancellationToken.None);
        var state = await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", 1, 9.5m, true), Admin, CancellationToken.None);

        Assert.Equal(("US", null, 7.25m), (country.Value!.CountryCode, country.Value.StateProvinceId, country.Value.Percentage));
        Assert.Equal(1, state.Value!.StateProvinceId);
        Assert.Equal(2, f.Audit.Events.Count(e => e == "tax.rate_created"));
        Assert.Equal(2, (await f.Create().GetRatesAsync(1, CancellationToken.None)).Value!.Count);
    }

    [Theory]
    [InlineData("US", 10.1234, "percentage")]
    [InlineData("US", -1, "percentage")]
    [InlineData("US", 100.5, "percentage")]
    [InlineData("ZZ", 10, "countryCode")]
    [InlineData("XX", 10, "countryCode")]
    [InlineData("", 10, "countryCode")]
    public async Task InvalidRatesAreRefusedWithTheFieldName(string country, double percentage, string field)
    {
        var f = new Fixture();

        var result = await f.Create().CreateRateAsync(1, new SaveTaxRateCommand(country, null, (decimal)percentage, true), Admin, CancellationToken.None);

        Assert.Contains(field, result.Errors.Keys);
        Assert.Empty(f.Store.Rates);
    }

    [Fact]
    public async Task AStateMustBelongToTheCountry()
    {
        var f = new Fixture();

        var result = await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("FR", 1, 10, true), Admin, CancellationToken.None);

        Assert.Contains("stateProvinceId", result.Errors.Keys);
    }

    [Fact]
    public async Task APercentageOfZeroAndOneHundredAreAllowed()
    {
        var f = new Fixture();

        Assert.True((await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", null, 0, true), Admin, CancellationToken.None)).Succeeded);
        Assert.True((await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("FR", null, 100, true), Admin, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task ACategoryHasOneRatePerPlace()
    {
        var f = new Fixture();
        await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", null, 7, true), Admin, CancellationToken.None);
        var second = (await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", 1, 9, true), Admin, CancellationToken.None)).Value!;

        Assert.Equal(TaxErrors.RateExists, (await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", null, 8, true), Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(TaxErrors.RateExists, (await f.Create().UpdateRateAsync(second.Id, new SaveTaxRateCommand("US", null, 9, true), Admin, CancellationToken.None)).ErrorCode);
        // Another category may have a rate for the same place.
        var reduced = (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("Reduced", 0), Admin, CancellationToken.None)).Value!;
        Assert.True((await f.Create().CreateRateAsync(reduced.Id, new SaveTaxRateCommand("US", null, 5, true), Admin, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task EachCategoryHasALimitOfRates()
    {
        var f = new Fixture();
        for (var i = 0; i < TaxLimits.MaxRatesPerCategory; i++) f.Store.Rates.Add(Rate(country: "Z" + i, id: 1000 + i));

        Assert.Equal(TaxErrors.RateLimit, (await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", null, 5, true), Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ARateCanBeChangedUnpublishedAndDeleted()
    {
        var f = new Fixture();
        var rate = (await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", null, 7, true), Admin, CancellationToken.None)).Value!;

        var updated = await f.Create().UpdateRateAsync(rate.Id, new SaveTaxRateCommand("US", 2, 8, false), Admin, CancellationToken.None);
        var deleted = await f.Create().DeleteRateAsync(rate.Id, Admin, CancellationToken.None);

        Assert.Equal((2, 8m, false), (updated.Value!.StateProvinceId, updated.Value.Percentage, updated.Value.Published));
        Assert.True(deleted.Succeeded);
        Assert.Empty(f.Store.Rates);
        Assert.Equal(["tax.rate_created", "tax.rate_updated", "tax.rate_deleted"], f.Audit.Events);
    }

    [Fact]
    public async Task RatesOfAMissingCategoryOrARateThatDoesNotExistAreNotFound()
    {
        var f = new Fixture();

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().GetRatesAsync(99, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().CreateRateAsync(99, new SaveTaxRateCommand("US", null, 5, true), Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().UpdateRateAsync(99, new SaveTaxRateCommand("US", null, 5, true), Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().DeleteRateAsync(99, Admin, CancellationToken.None)).ErrorCode);
    }

    // ---- Service: products ----

    [Fact]
    public async Task AProductWithoutAssignmentIsInTheDefaultCategory()
    {
        var f = new Fixture();

        var view = (await f.Create().GetProductAsync(1, CancellationToken.None)).Value!;

        Assert.Equal(("Mug", 1, "Standard", false), (view.ProductName, view.TaxCategoryId, view.TaxCategoryName, view.Assigned));
    }

    [Fact]
    public async Task AssigningAndClearingACategoryMovesTheProduct()
    {
        var f = new Fixture();
        var reduced = (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("Reduced", 0), Admin, CancellationToken.None)).Value!;

        var assigned = (await f.Create().SetProductCategoryAsync(1, reduced.Id, Admin, CancellationToken.None)).Value!;
        var cleared = (await f.Create().SetProductCategoryAsync(1, null, Admin, CancellationToken.None)).Value!;

        Assert.Equal(("Reduced", true), (assigned.TaxCategoryName, assigned.Assigned));
        Assert.Equal(("Standard", false), (cleared.TaxCategoryName, cleared.Assigned));
        Assert.Equal(2, f.Audit.Events.Count(e => e == "tax.product_assigned"));
    }

    [Fact]
    public async Task AProductOrACategoryThatDoesNotExistIsRefused()
    {
        var f = new Fixture();

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().SetProductCategoryAsync(99, 1, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().GetProductAsync(99, CancellationToken.None)).ErrorCode);
        Assert.Contains("taxCategoryId", (await f.Create().SetProductCategoryAsync(1, 99, Admin, CancellationToken.None)).Errors.Keys);
        Assert.Empty(f.Store.Assignments);
    }

    // ---- Service: calculating for checkout ----

    [Fact]
    public async Task TheCalculationUsesTheCategoryOfEachProductAndTheDestination()
    {
        var f = new Fixture();
        var reduced = (await f.Create().CreateCategoryAsync(new SaveTaxCategoryCommand("Reduced", 0), Admin, CancellationToken.None)).Value!;
        await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", null, 10, true), Admin, CancellationToken.None);
        await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", 1, 12, true), Admin, CancellationToken.None);
        await f.Create().CreateRateAsync(reduced.Id, new SaveTaxRateCommand("US", null, 5, true), Admin, CancellationToken.None);
        await f.Create().SetProductCategoryAsync(2, reduced.Id, Admin, CancellationToken.None);
        TaxableLine[] lines = [new TaxableLine(Shop, 1, 100m), new TaxableLine(OtherShop, 2, 100m)];

        var newYork = await f.Create().CalculateAsync("US", 2, lines, NoDiscount, CancellationToken.None);
        var california = await f.Create().CalculateAsync("us", 1, lines, NoDiscount, CancellationToken.None);
        var france = await f.Create().CalculateAsync("FR", null, lines, NoDiscount, CancellationToken.None);

        Assert.Equal([10m, 5m], newYork.Lines.Select(l => l.Tax));
        Assert.Equal([12m, 5m], california.Lines.Select(l => l.Tax));
        Assert.Equal(17m, california.Total);
        Assert.Equal(0m, france.Total);
        Assert.Equal((10m, 5m), (newYork.ShopTax[Shop], newYork.ShopTax[OtherShop]));
    }

    [Fact]
    public async Task TheCalculationTakesTheDiscountOutOfTheBase()
    {
        var f = new Fixture();
        await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", null, 10, true), Admin, CancellationToken.None);

        var result = await f.Create().CalculateAsync("US", null, [new TaxableLine(Shop, 1, 100m)], new Dictionary<int, decimal> { [Shop] = 20m }, CancellationToken.None);

        Assert.Equal((80m, 8m), (result.Lines[0].Base, result.Total));
    }

    [Fact]
    public async Task NoLinesMeanNoTaxAndUnpublishedRatesAreNotUsed()
    {
        var f = new Fixture();
        await f.Create().CreateRateAsync(1, new SaveTaxRateCommand("US", null, 10, false), Admin, CancellationToken.None);

        Assert.Equal(0m, (await f.Create().CalculateAsync("US", null, [], NoDiscount, CancellationToken.None)).Total);
        Assert.Equal(0m, (await f.Create().CalculateAsync("US", null, [new TaxableLine(Shop, 1, 100m)], NoDiscount, CancellationToken.None)).Total);
    }
}
