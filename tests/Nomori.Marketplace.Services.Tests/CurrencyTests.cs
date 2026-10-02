using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Catalog;
using Nomori.Marketplace.Services.Directory;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class CurrencyTests
{
    private const int Admin = 1;

    private static Currency Make(string code, decimal rate, int decimals = 2) =>
        new() { Id = 1, Code = code, Name = code, DecimalPlaces = decimals, RateToPrimary = rate };

    // ---- Pure rules ----

    [Theory]
    [InlineData(2.5, 0, 3)]
    [InlineData(-2.5, 0, -3)]
    [InlineData(2.345, 2, 2.35)]
    [InlineData(2.344, 2, 2.34)]
    [InlineData(1.0005, 3, 1.001)]
    public void RoundingGoesAwayFromZeroOnTheMidpoint(double amount, int decimals, double expected) =>
        Assert.Equal((decimal)expected, CurrencyRules.Round((decimal)amount, decimals));

    [Fact]
    public void ConversionGoesThroughThePrimaryCurrency()
    {
        var usd = Make("USD", 1);
        var eur = Make("EUR", 0.92m);
        var vnd = Make("VND", 25000m, 0);

        Assert.Equal(9.20m, CurrencyRules.Convert(10m, usd, eur));
        Assert.Equal(10.00m, CurrencyRules.Convert(9.20m, eur, usd));
        Assert.Equal(250000m, CurrencyRules.Convert(10m, usd, vnd));
        // Between two non-primary currencies: EUR 9.20 is USD 10 is VND 250,000.
        Assert.Equal(250000m, CurrencyRules.Convert(9.20m, eur, vnd));
        Assert.Equal(0m, CurrencyRules.Convert(0m, usd, eur));
    }

    [Theory]
    [InlineData(12, 0)]
    [InlineData(12.5, 1)]
    [InlineData(12.505, 3)]
    [InlineData(0.0001, 4)]
    public void DecimalPlacesAreCountedWithoutTrailingZeros(double amount, int expected) =>
        Assert.Equal(expected, CurrencyRules.DecimalPlacesOf((decimal)amount));

    [Fact]
    public void TrailingZerosOfADecimalDoNotCount()
    {
        Assert.Equal(0, CurrencyRules.DecimalPlacesOf(12.000m));
        Assert.Equal(2, CurrencyRules.DecimalPlacesOf(12.340m));
        Assert.True(CurrencyRules.HasValidScale(10.10m, 2));
        Assert.True(CurrencyRules.HasValidScale(10.100m, 2));
        Assert.False(CurrencyRules.HasValidScale(10.101m, 2));
        Assert.False(CurrencyRules.HasValidScale(0.5m, 0));
        Assert.True(CurrencyRules.HasValidScale(500m, 0));
    }

    [Fact]
    public void RebasingKeepsEveryConversionMeaningful()
    {
        // 1 USD = 0.92 EUR, so 1 EUR = 1.08695652 USD.
        Assert.Equal(1.08695652m, CurrencyRules.Rebase(1m, 0.92m));
        Assert.Equal(1m, CurrencyRules.Rebase(0.92m, 0.92m));
    }

    // ---- Service ----

    private sealed class Fixture
    {
        public FakeCurrencyStore Store { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public CurrencyService Create() => new(Store, Audit, new TestClock());
    }

    private static SaveCurrencyCommand Eur(decimal rate = 0.92m, bool published = true) =>
        new("eur", "Euro", "€", 2, rate, published, 1);

    [Fact]
    public async Task CreateNormalizesTheCodeAndIsNeverPrimary()
    {
        var f = new Fixture();

        var result = await f.Create().CreateAsync(Eur(), Admin, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(("EUR", false, 0.92m), (result.Value!.Code, result.Value.IsPrimary, result.Value.RateToPrimary));
        Assert.Single(f.Store.Currencies, c => c.IsPrimary);
        Assert.Contains(f.Audit.Entries, e => e.Event == "currency.created");
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDX")]
    [InlineData("U5D")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ACodeNeedsThreeLetters(string? code)
    {
        var result = await new Fixture().Create().CreateAsync(Eur() with { Code = code }, Admin, CancellationToken.None);

        Assert.Contains("code", result.Errors.Keys);
    }

    [Fact]
    public async Task ACodeIsUniqueIgnoringCase()
    {
        var f = new Fixture();

        var result = await f.Create().CreateAsync(Eur() with { Code = "usd" }, Admin, CancellationToken.None);

        Assert.Equal(DirectoryErrors.CurrencyCodeExists, result.ErrorCode);
    }

    [Fact]
    public async Task FieldsAreValidated()
    {
        var service = new Fixture().Create();

        Task<CatalogResult<Currency>> Try(SaveCurrencyCommand c) => service.CreateAsync(c, Admin, CancellationToken.None);

        Assert.Contains("name", (await Try(Eur() with { Name = " " })).Errors.Keys);
        Assert.Contains("name", (await Try(Eur() with { Name = new string('n', 101) })).Errors.Keys);
        Assert.Contains("symbol", (await Try(Eur() with { Symbol = new string('s', 11) })).Errors.Keys);
        Assert.Contains("decimalPlaces", (await Try(Eur() with { DecimalPlaces = -1 })).Errors.Keys);
        Assert.Contains("decimalPlaces", (await Try(Eur() with { DecimalPlaces = 5 })).Errors.Keys);
        Assert.Contains("rate", (await Try(Eur(0m))).Errors.Keys);
        Assert.Contains("rate", (await Try(Eur(-1m))).Errors.Keys);
        Assert.Contains("rate", (await Try(Eur(1_000_000_001m))).Errors.Keys);
        Assert.Contains("rate", (await Try(Eur(0.000000001m))).Errors.Keys);
        Assert.True((await Try(Eur(0.00000001m))).Succeeded);
    }

    [Fact]
    public async Task UpdatingARateStampsTheRateTime()
    {
        var f = new Fixture();
        var service = f.Create();
        var created = (await service.CreateAsync(Eur(), Admin, CancellationToken.None)).Value!;
        var before = created.RateUpdatedOnUtc;
        f.Store.Currencies.Single(c => c.Id == created.Id).RateUpdatedOnUtc = before.AddDays(-5);

        var same = await service.UpdateAsync(created.Id, Eur() with { Name = "Euro 2" }, Admin, CancellationToken.None);
        Assert.Equal(before.AddDays(-5), same.Value!.RateUpdatedOnUtc);

        var changed = await service.UpdateAsync(created.Id, Eur(0.95m), Admin, CancellationToken.None);
        Assert.Equal(0.95m, changed.Value!.RateToPrimary);
        Assert.Equal(new TestClock().UtcNow, changed.Value.RateUpdatedOnUtc);
    }

    [Fact]
    public async Task ThePrimaryKeepsRateOneAndStaysPublished()
    {
        var f = new Fixture();
        var service = f.Create();

        var updated = await service.UpdateAsync(1, new SaveCurrencyCommand(null, "Dollar", "US$", 2, 5m, true, 0), Admin, CancellationToken.None);
        Assert.Equal((1m, "Dollar"), (updated.Value!.RateToPrimary, updated.Value.Name));

        var unpublish = await service.UpdateAsync(1, new SaveCurrencyCommand(null, "Dollar", "$", 2, 1m, false, 0), Admin, CancellationToken.None);
        Assert.Equal(DirectoryErrors.CurrencyPrimaryRequired, unpublish.ErrorCode);
    }

    [Fact]
    public async Task ThePrimaryDecimalsAreLockedOnceProductsExist()
    {
        var f = new Fixture();
        var service = f.Create();
        var change = new SaveCurrencyCommand(null, "US Dollar", "$", 0, 1m, true, 0);

        Assert.True((await service.UpdateAsync(1, change, Admin, CancellationToken.None)).Succeeded);

        f.Store.ProductsExist = true;
        var locked = await service.UpdateAsync(1, change with { DecimalPlaces = 3 }, Admin, CancellationToken.None);
        Assert.Equal(DirectoryErrors.CurrencyPrimaryLocked, locked.ErrorCode);
        // Other fields stay editable.
        Assert.True((await service.UpdateAsync(1, change with { Name = "Dollar" }, Admin, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task OtherCurrenciesCanBeUnpublishedAndDeletedButNotThePrimary()
    {
        var f = new Fixture();
        var service = f.Create();
        var eur = (await service.CreateAsync(Eur(), Admin, CancellationToken.None)).Value!;

        Assert.False((await service.UpdateAsync(eur.Id, Eur(published: false), Admin, CancellationToken.None)).Value!.Published);
        Assert.Equal(DirectoryErrors.CurrencyPrimaryRequired, (await service.DeleteAsync(1, Admin, CancellationToken.None)).ErrorCode);
        Assert.True((await service.DeleteAsync(eur.Id, Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(CatalogErrors.NotFound, (await service.DeleteAsync(eur.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.UpdateAsync(999, Eur(), Admin, CancellationToken.None)).ErrorCode);
        Assert.Contains(f.Audit.Entries, e => e.Event == "currency.deleted");
    }

    [Fact]
    public async Task MakePrimaryRebasesTheRatesAndSwapsTheFlag()
    {
        var f = new Fixture();
        var service = f.Create();
        var eur = (await service.CreateAsync(Eur(0.92m), Admin, CancellationToken.None)).Value!;
        var vnd = (await service.CreateAsync(new SaveCurrencyCommand("VND", "Dong", "₫", 0, 25000m, false, 2), Admin, CancellationToken.None)).Value!;

        var result = await service.MakePrimaryAsync(eur.Id, Admin, CancellationToken.None);

        Assert.True(result.Succeeded);
        var all = f.Store.Currencies;
        Assert.Single(all, c => c.IsPrimary);
        Assert.Equal(1m, all.Single(c => c.Id == eur.Id).RateToPrimary);
        Assert.Equal(1.08695652m, all.Single(c => c.Id == 1).RateToPrimary);
        Assert.False(all.Single(c => c.Id == 1).IsPrimary);
        // 1 EUR is 25,000 / 0.92 dong.
        Assert.Equal(CurrencyRules.Rebase(25000m, 0.92m), all.Single(c => c.Id == vnd.Id).RateToPrimary);
        Assert.Contains(f.Audit.Entries, e => e.Event == "currency.primary_changed");
    }

    [Fact]
    public async Task MakePrimaryIsRefusedWhenProductsExistAndIsANoOpWhenAlreadyPrimary()
    {
        var f = new Fixture();
        var service = f.Create();
        var eur = (await service.CreateAsync(Eur(), Admin, CancellationToken.None)).Value!;

        Assert.True((await service.MakePrimaryAsync(1, Admin, CancellationToken.None)).Succeeded);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Event == "currency.primary_changed");

        f.Store.ProductsExist = true;
        Assert.Equal(DirectoryErrors.CurrencyPrimaryLocked, (await service.MakePrimaryAsync(eur.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.MakePrimaryAsync(999, Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task OnlyPublishedCurrenciesAreListedPubliclyPrimaryFirst()
    {
        var f = new Fixture();
        var service = f.Create();
        await service.CreateAsync(new SaveCurrencyCommand("GBP", "Pound", "£", 2, 0.8m, true, 5), Admin, CancellationToken.None);
        await service.CreateAsync(new SaveCurrencyCommand("EUR", "Euro", "€", 2, 0.92m, true, 1), Admin, CancellationToken.None);
        await service.CreateAsync(new SaveCurrencyCommand("JPY", "Yen", "¥", 0, 150m, false, 0), Admin, CancellationToken.None);

        Assert.Equal(["USD", "EUR", "GBP"], (await service.GetPublishedAsync(CancellationToken.None)).Select(c => c.Code));
        Assert.Equal(4, (await service.GetAllAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task AuditHoldsCodesAndNumbersOnly()
    {
        var f = new Fixture();
        var created = (await f.Create().CreateAsync(Eur() with { Name = "Secret name" }, Admin, CancellationToken.None)).Value!;
        await f.Create().UpdateAsync(created.Id, Eur(0.9m) with { Name = "Secret name" }, Admin, CancellationToken.None);

        foreach (var entry in f.Audit.Entries.Where(e => e.Event.StartsWith("currency.", StringComparison.Ordinal)))
            Assert.DoesNotContain("Secret", entry.Details?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task ThePrimaryProviderFallsBackToDollarsOnlyWhenThereIsNone()
    {
        var f = new Fixture();
        var provider = new PrimaryCurrencyProvider(f.Store);

        Assert.Equal("USD", (await provider.GetPrimaryAsync(CancellationToken.None)).Code);
        f.Store.Currencies[0].Code = "EUR";
        Assert.Equal("EUR", (await provider.GetPrimaryAsync(CancellationToken.None)).Code);
        f.Store.Currencies.Clear();
        Assert.Equal(("USD", 2), ((await provider.GetPrimaryAsync(CancellationToken.None)) is var c ? (c.Code, c.DecimalPlaces) : default));
    }

    // ---- Prices follow the primary currency ----

    private sealed class PriceFixture(int decimals)
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public FakeAttributeStore Attributes { get; } = new();

        public ProductService Create() => new(
            Products, new FakeInventoryStore(Products), new FakePrimaryCurrency(decimals), new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors,
            new NoMembers(), new FakeMediaStore(), new FakeTaxonomy(), new RecordingAuditLog(), new RecordingEmailSender(),
            TestOptions.Email(false), NullLog<ProductService>.Instance, new TestClock());

        public VendorProductDetailsService CreateDetails() => new(
            Products, Vendors, Attributes, new ProductAttributeService(Attributes), new FakeSpecificationStore(),
            new FakeInventoryStore(Products, Attributes), new FakePrimaryCurrency(decimals), new RecordingAuditLog(), new TestClock());

        public PriceFixture WithShop()
        {
            Vendors.Vendors.Add(new Vendor { Id = 5, Name = "Shop", Active = true });
            return this;
        }
    }

    [Fact]
    public async Task SellerPricesCannotHaveMoreDecimalsThanThePrimaryCurrency()
    {
        var service = new PriceFixture(2).WithShop().Create();

        Task<CatalogResult<Product>> Create(decimal price, decimal oldPrice = 0) =>
            service.CreateForVendorAsync(5, new SaveVendorProductCommand("Mug", null, null, price, oldPrice, 1, [1], []), 10, CancellationToken.None);

        Assert.True((await Create(10.99m)).Succeeded);
        Assert.True((await Create(10.990m)).Succeeded);
        var price = await Create(10.999m);
        Assert.Contains("price", price.Errors.Keys);
        Assert.Contains("USD", price.Errors["price"][0]);
        Assert.Contains("oldPrice", (await Create(10m, 12.345m)).Errors.Keys);
    }

    [Fact]
    public async Task AZeroDecimalCurrencyRefusesFractions()
    {
        var service = new PriceFixture(0).WithShop().Create();

        var result = await service.CreateForVendorAsync(5, new SaveVendorProductCommand("Mug", null, null, 10.5m, 0, 1, [1], []), 10, CancellationToken.None);

        Assert.Contains("price", result.Errors.Keys);
        Assert.True((await service.CreateForVendorAsync(5, new SaveVendorProductCommand("Mug", null, null, 10m, 0, 1, [1], []), 10, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AdminPricesFollowTheSameRule()
    {
        var f = new PriceFixture(2).WithShop();
        var service = f.Create();

        var bad = await service.CreateAsync(new CreateProductCommand("Admin", null, null, 5.555m, 0, 1, true, 5, false, 0, [1], []), 1, CancellationToken.None);
        Assert.Contains("price", bad.Errors.Keys);

        var ok = await service.CreateAsync(new CreateProductCommand("Admin", null, null, 5.55m, 0, 1, true, 5, false, 0, [1], []), 1, CancellationToken.None);
        Assert.True(ok.Succeeded);

        var update = await service.UpdateAsync(new UpdateProductCommand(ok.Value!.Id, "Admin", null, null, 5.555m, 0, 1, true, null, false, 0, [1], []), 1, CancellationToken.None);
        Assert.Contains("price", update.Errors.Keys);
    }

    [Fact]
    public async Task VariantAdjustmentsAndOverridePricesFollowTheSameRule()
    {
        var f = new PriceFixture(2).WithShop();
        f.Attributes.Attributes.Add(new ProductAttributeSpec { Id = 1, Name = "Color" });
        var id = (await f.Create().CreateForVendorAsync(5, new SaveVendorProductCommand("Shirt", null, null, 10m, 0, 1, [1], []), 10, CancellationToken.None)).Value!.Id;
        var details = f.CreateDetails();

        Task<CatalogResult<ProductAttributeDetail>> Try(decimal adjustment, decimal? overridden) => details.SetVariantsAsync(5, id,
            new SaveVariantsCommand([new VariantAttributeInput(1, true, [new VariantValueInput("Red", null, adjustment)])],
                [new VariantCombinationInput([0], null, 1, overridden)]), 10, CancellationToken.None);

        Assert.Contains("attributes", (await Try(0.555m, null)).Errors.Keys);
        Assert.Contains("combinations", (await Try(0m, 9.999m)).Errors.Keys);
        Assert.True((await Try(0.55m, 9.99m)).Succeeded);
    }
}
