using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class PricingTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Seller = 10;

    // The test clock is 2026-01-01 00:00 UTC.
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---- Pure rules ----

    [Fact]
    public void ASpecialPriceIsActiveOnlyInsideItsWindow()
    {
        Product Make(DateTime? start, DateTime? end) => new() { Price = 10, SpecialPrice = 8, SpecialPriceStartUtc = start, SpecialPriceEndUtc = end };

        Assert.True(PriceRules.IsSpecialActive(Make(null, null), Now));
        Assert.True(PriceRules.IsSpecialActive(Make(Now.AddDays(-1), Now.AddDays(1)), Now));
        Assert.True(PriceRules.IsSpecialActive(Make(Now, null), Now));
        Assert.False(PriceRules.IsSpecialActive(Make(Now.AddDays(1), null), Now));
        Assert.False(PriceRules.IsSpecialActive(Make(null, Now), Now));
        Assert.False(PriceRules.IsSpecialActive(new Product { Price = 10 }, Now));
        Assert.False(PriceRules.IsSpecialActive(new Product { Price = 10, SpecialPrice = 0 }, Now));
    }

    [Fact]
    public void TheCurrentPriceIsTheSpecialPriceWhileItsWindowIsOpen()
    {
        var product = new Product { Price = 10, SpecialPrice = 8, SpecialPriceEndUtc = Now.AddDays(1) };

        Assert.Equal(8m, PriceRules.CurrentPrice(product, Now));
        Assert.Equal(10m, PriceRules.CurrentPrice(product, Now.AddDays(2)));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(4, null)]
    [InlineData(5, 7)]
    [InlineData(9, 7)]
    [InlineData(10, 6)]
    [InlineData(500, 6)]
    public void TheTierPriceIsThatOfTheHighestStepReached(int quantity, int? expected)
    {
        TierPrice[] tiers = [new(10, 6m), new(5, 7m)];

        Assert.Equal(expected is null ? null : (decimal?)expected, PriceRules.TierPriceFor(tiers, quantity));
    }

    [Fact]
    public void TheLowestOfRegularSpecialAndTierWins()
    {
        Assert.Equal((10m, null, PriceRule.Base), PriceRules.Compose(10, null, null, 0, null, 0));
        Assert.Equal((8m, 10m, PriceRule.Special), PriceRules.Compose(10, 8, null, 0, null, 0));
        Assert.Equal((7m, 10m, PriceRule.Tier), PriceRules.Compose(10, 8, 7, 0, null, 0));
        // A tier price above the special price does not raise the price.
        Assert.Equal((8m, 10m, PriceRule.Special), PriceRules.Compose(10, 8, 9, 0, null, 0));
    }

    [Fact]
    public void AdjustmentsAreAddedAndAnOverrideWinsOverEverything()
    {
        Assert.Equal((10.5m, null, PriceRule.Base), PriceRules.Compose(10, null, null, 0.5m, null, 0));
        Assert.Equal((8.5m, 10.5m, PriceRule.Special), PriceRules.Compose(10, 8, null, 0.5m, null, 0));
        Assert.Equal((20m, null, PriceRule.VariantOverride), PriceRules.Compose(10, 8, 7, 0.5m, 20m, 30m));
    }

    [Fact]
    public void TheComparePriceIsTheRegularOrOldPriceOnlyWhenHigher()
    {
        Assert.Equal(12m, PriceRules.Compose(10, null, null, 0, null, 12).Compare);
        Assert.Null(PriceRules.Compose(10, null, null, 0, null, 9).Compare);
        // On sale: the regular price is shown even when an old price exists.
        Assert.Equal(10m, PriceRules.Compose(10, 8, null, 0, null, 12).Compare);
    }

    // ---- Fixtures ----

    private sealed class Fixture(int decimals = 2)
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public FakeAttributeStore Attributes { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture Init()
        {
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Shop", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Other", Active = true });
            Attributes.Attributes.Add(new ProductAttributeSpec { Id = 1, Name = "Color" });
            Attributes.Attributes.Add(new ProductAttributeSpec { Id = 2, Name = "Size" });
            return this;
        }

        public ProductPricingService Pricing() => new(Products, Vendors, new FakePrimaryCurrency(decimals), Audit, new TestClock());

        public PriceCalculationService Calculator() => new(Products, Attributes, new FakePrimaryCurrency(decimals), new TestClock());

        public ProductService CreateProducts() => new(
            Products, new FakeInventoryStore(Products), new FakePrimaryCurrency(decimals), new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors,
            new NoMembers(), new FakeMediaStore(), new FakeTaxonomy(), Audit, new RecordingEmailSender(), TestOptions.Email(false), NullLog<ProductService>.Instance, new TestClock());

        public async Task<int> ProductAsync(decimal price = 10, decimal oldPrice = 0)
        {
            var result = await CreateProducts().CreateForVendorAsync(Shop,
                new SaveVendorProductCommand("Mug", null, null, price, oldPrice, 3, [1], []), Seller, CancellationToken.None);
            return result.Value!.Id;
        }

        /// <summary>Saves variants through the real service so combination keys are built the usual way.</summary>
        public async Task<ProductAttributeDetail> VariantsAsync(int productId, SaveVariantsCommand command)
        {
            var details = new VendorProductDetailsService(Products, Vendors, Attributes, new ProductAttributeService(Attributes), new FakeSpecificationStore(),
                new FakeInventoryStore(Products, Attributes), new FakePrimaryCurrency(decimals), Audit, new TestClock());
            return (await details.SetVariantsAsync(Shop, productId, command, Seller, CancellationToken.None)).Value!;
        }
    }

    private static Task<CatalogResult<ProductPricing>> Save(Fixture f, int productId, decimal? special, DateTime? start = null, DateTime? end = null, int vendorId = Shop, params TierPrice[] tiers) =>
        f.Pricing().SetForVendorAsync(vendorId, productId, new SavePricingCommand(special, start, end, tiers), Seller, CancellationToken.None);

    // ---- Saving pricing ----

    [Fact]
    public async Task PricingIsSavedAndReadBackWithSortedTiers()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);

        var result = await Save(f, id, 8, Now, Now.AddDays(5), Shop, new TierPrice(10, 6), new TierPrice(5, 7));

        Assert.True(result.Succeeded);
        Assert.Equal(8m, result.Value!.SpecialPrice);
        Assert.Equal([5, 10], result.Value.TierPrices.Select(t => t.Quantity));
        Assert.Equal(8m, f.Products.Products.Single(p => p.Id == id).SpecialPrice);
        Assert.Contains(f.Audit.Entries, e => e.Event == "product.pricing_changed");
        Assert.Equal(2, (await f.Pricing().GetForVendorAsync(Shop, id, CancellationToken.None)).Value!.TierPrices.Count);
    }

    [Fact]
    public async Task ClearingTheSpecialPriceAlsoClearsItsWindow()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        await Save(f, id, 8, Now, Now.AddDays(5));

        var cleared = await Save(f, id, null);

        Assert.True(cleared.Succeeded);
        var product = f.Products.Products.Single(p => p.Id == id);
        Assert.Equal((null, null, null), (product.SpecialPrice, product.SpecialPriceStartUtc, product.SpecialPriceEndUtc));
        Assert.Empty(cleared.Value!.TierPrices);
    }

    [Fact]
    public async Task DatesWithoutASpecialPriceAreRefused()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);

        Assert.Contains("specialPrice", (await Save(f, id, null, Now)).Errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(8.555)]
    public async Task TheSpecialPriceMustBePositiveLowerThanThePriceAndWithinTheDecimals(double special)
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);

        Assert.Contains("specialPrice", (await Save(f, id, (decimal)special)).Errors.Keys);
    }

    [Fact]
    public async Task TheSpecialWindowMustEndAfterItStarts()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);

        Assert.Contains("specialPriceEndUtc", (await Save(f, id, 8, Now, Now)).Errors.Keys);
        Assert.Contains("specialPriceEndUtc", (await Save(f, id, 8, Now, Now.AddDays(-1))).Errors.Keys);
        Assert.True((await Save(f, id, 8, Now, null)).Succeeded);
        Assert.True((await Save(f, id, 8, null, Now)).Succeeded);
    }

    [Fact]
    public async Task TierPricesFollowTheirRules()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);

        Task<CatalogResult<ProductPricing>> Try(params TierPrice[] tiers) => Save(f, id, null, null, null, Shop, tiers);

        Assert.Contains("tierPrices", (await Try(new TierPrice(1, 9))).Errors.Keys);
        Assert.Contains("tierPrices", (await Try(new TierPrice(10_001, 9))).Errors.Keys);
        Assert.Contains("tierPrices", (await Try(new TierPrice(5, 0))).Errors.Keys);
        Assert.Contains("tierPrices", (await Try(new TierPrice(5, 10))).Errors.Keys);
        Assert.Contains("tierPrices", (await Try(new TierPrice(5, 8.555m))).Errors.Keys);
        Assert.Contains("tierPrices", (await Try(new TierPrice(5, 8), new TierPrice(5, 7))).Errors.Keys);
        Assert.Contains("tierPrices", (await Try(new TierPrice(5, 8), new TierPrice(10, 8))).Errors.Keys);
        Assert.Contains("tierPrices", (await Try(new TierPrice(5, 8), new TierPrice(10, 9))).Errors.Keys);
        Assert.Contains("tierPrices", (await Try(Enumerable.Range(2, 21).Select(i => new TierPrice(i, 9m - i * 0.01m)).ToArray())).Errors.Keys);
        Assert.True((await Try(Enumerable.Range(2, 20).Select(i => new TierPrice(i, 9m - i * 0.01m)).ToArray())).Succeeded);
    }

    [Fact]
    public async Task ASavedListReplacesTheOldOne()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        await Save(f, id, null, null, null, Shop, new TierPrice(5, 8), new TierPrice(10, 7));

        await Save(f, id, null, null, null, Shop, new TierPrice(3, 9));

        Assert.Equal([3], (await f.Products.GetTierPricesAsync(id, CancellationToken.None)).Select(t => t.Quantity));
    }

    [Fact]
    public async Task PricingIsIsolatedPerShopAndBlockedForInactiveShops()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);

        Assert.Equal(CatalogErrors.NotFound, (await Save(f, id, 8, vendorId: OtherShop)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Pricing().GetForVendorAsync(OtherShop, id, CancellationToken.None)).ErrorCode);

        f.Vendors.Vendors.Single(v => v.Id == Shop).Active = false;
        Assert.Equal(CatalogErrors.Forbidden, (await Save(f, id, 8)).ErrorCode);
    }

    [Fact]
    public async Task PricingCanBeEditedWhileHidden()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        await f.CreateProducts().HideAsync(id, "Reason", 1, CancellationToken.None);

        Assert.True((await Save(f, id, 8)).Succeeded);
    }

    // ---- Changing the regular price ----

    private static SaveVendorProductCommand Edit(decimal price) => new("Mug", null, null, price, 0, 3, [1], []);

    [Fact]
    public async Task TheRegularPriceCannotDropToOrBelowTheSpecialOrATierPrice()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        await Save(f, id, 8, null, null, Shop, new TierPrice(5, 7));
        var products = f.CreateProducts();

        Task<CatalogResult<Product>> Update(decimal price) => products.UpdateForVendorAsync(Shop, id, Edit(price), Seller, CancellationToken.None);

        Assert.Contains("price", (await Update(8)).Errors.Keys);
        Assert.Contains("price", (await Update(7.5m)).Errors.Keys);
        Assert.Contains("price", (await Update(7)).Errors.Keys);
        Assert.True((await Update(9)).Succeeded);
    }

    [Fact]
    public async Task RaisingTheRegularPriceIsAlwaysAllowedAndAudited()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        await Save(f, id, 8);

        var raised = await f.CreateProducts().UpdateForVendorAsync(Shop, id, Edit(12), Seller, CancellationToken.None);

        Assert.True(raised.Succeeded);
        var entry = f.Audit.Entries.Last(e => e.Event == "product.price_changed");
        Assert.Contains("from = 10", entry.Details!.ToString());
        Assert.Contains("to = 12", entry.Details.ToString());
        // An unchanged price writes no price audit.
        var before = f.Audit.Entries.Count(e => e.Event == "product.price_changed");
        await f.CreateProducts().UpdateForVendorAsync(Shop, id, Edit(12), Seller, CancellationToken.None);
        Assert.Equal(before, f.Audit.Entries.Count(e => e.Event == "product.price_changed"));
    }

    [Fact]
    public async Task TheAdminFormObeysTheSameRuleAndNeverBlanksPricing()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        await Save(f, id, 8);
        var products = f.CreateProducts();
        UpdateProductCommand Admin(decimal price) => new(id, "Mug", null, null, price, 0, 3, true, null, false, 0, [1], []);

        Assert.Contains("price", (await products.UpdateAsync(Admin(8), 1, CancellationToken.None)).Errors.Keys);
        Assert.True((await products.UpdateAsync(Admin(9), 1, CancellationToken.None)).Succeeded);
        Assert.Equal(8m, f.Products.Products.Single(p => p.Id == id).SpecialPrice);
    }

    // ---- Quotes ----

    private static Task<CatalogResult<PriceQuote>> Quote(Fixture f, int productId, int quantity = 1, params int[] valueIds) =>
        f.Calculator().QuoteAsync(new PriceRequest(productId, quantity, valueIds), CancellationToken.None);

    [Fact]
    public async Task APlainProductIsQuotedAtItsPrice()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10, oldPrice: 12);

        var quote = (await Quote(f, id, 3)).Value!;

        Assert.Equal((10m, 12m, 30m, PriceRule.Base, "USD"), (quote.UnitPrice, quote.ComparePrice, quote.LineTotal, quote.AppliedRule, quote.CurrencyCode));
        Assert.Null(quote.CombinationId);
    }

    [Fact]
    public async Task TheSpecialPriceAppliesInsideItsWindowAndTierPricesByQuantity()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        await Save(f, id, 8, Now.AddDays(-1), Now.AddDays(1), Shop, new TierPrice(5, 7), new TierPrice(10, 6));

        var one = (await Quote(f, id, 1)).Value!;
        Assert.Equal((8m, 10m, PriceRule.Special), (one.UnitPrice, one.ComparePrice, one.AppliedRule));

        var five = (await Quote(f, id, 5)).Value!;
        Assert.Equal((7m, 35m, PriceRule.Tier), (five.UnitPrice, five.LineTotal, five.AppliedRule));

        var many = (await Quote(f, id, 12)).Value!;
        Assert.Equal((6m, 72m), (many.UnitPrice, many.LineTotal));

        // After the window the special price is gone but tier prices stay.
        f.Products.Products.Single(p => p.Id == id).SpecialPriceEndUtc = Now.AddSeconds(-1);
        Assert.Equal((10m, PriceRule.Base), ((await Quote(f, id, 1)).Value!.UnitPrice, (await Quote(f, id, 1)).Value!.AppliedRule));
        Assert.Equal(7m, (await Quote(f, id, 5)).Value!.UnitPrice);
    }

    [Fact]
    public async Task QuantityIsValidatedAndUnknownProductsAreNotFound()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);

        Assert.Contains("quantity", (await Quote(f, id, 0)).Errors.Keys);
        Assert.Contains("quantity", (await Quote(f, id, PricingLimits.MaxQuantity + 1)).Errors.Keys);
        Assert.True((await Quote(f, id, PricingLimits.MaxQuantity)).Succeeded);
        Assert.Equal(CatalogErrors.NotFound, (await Quote(f, 9999)).ErrorCode);
    }

    private static SaveVariantsCommand ColorSize(decimal redAdjustment = 0, decimal? overrideRedS = null) => new(
        [new VariantAttributeInput(1, true, [new VariantValueInput("Red", null, redAdjustment), new VariantValueInput("Blue")]),
         new VariantAttributeInput(2, true, [new VariantValueInput("S"), new VariantValueInput("M", null, 1)])],
        [new VariantCombinationInput([0, 0], null, 5, overrideRedS), new VariantCombinationInput([0, 1], null, 5, null), new VariantCombinationInput([1, 0], null, 5, null)]);

    [Fact]
    public async Task VariantsNeedAFullSelectionOfAnExistingCombination()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        var detail = await f.VariantsAsync(id, ColorSize());
        int Value(string color) => detail.Mappings.SelectMany(m => m.Values).Single(v => v.Name == color).Id;

        Assert.Contains("valueIds", (await Quote(f, id, 1)).Errors.Keys);
        Assert.Contains("valueIds", (await Quote(f, id, 1, Value("Red"))).Errors.Keys);
        Assert.Contains("valueIds", (await Quote(f, id, 1, Value("Red"), Value("Blue"))).Errors.Keys);
        // Blue with M was never created.
        Assert.Contains("valueIds", (await Quote(f, id, 1, Value("Blue"), Value("M"))).Errors.Keys);
        Assert.Contains("valueIds", (await Quote(f, id, 1, 99999, Value("S"))).Errors.Keys);
        Assert.True((await Quote(f, id, 1, Value("Blue"), Value("S"))).Succeeded);
    }

    [Fact]
    public async Task VariantAdjustmentsAreAddedToTheBestPrice()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        var detail = await f.VariantsAsync(id, ColorSize(redAdjustment: 0.5m));
        int Value(string name) => detail.Mappings.SelectMany(m => m.Values).Single(v => v.Name == name).Id;
        await Save(f, id, 8);

        var redM = (await Quote(f, id, 1, Value("Red"), Value("M"))).Value!;

        // 8 (special) + 0.5 (Red) + 1 (M); the regular price with the same adjustments is struck through.
        Assert.Equal((9.5m, 11.5m, 11.5m), (redM.UnitPrice, redM.ComparePrice, redM.RegularPrice));
        Assert.NotNull(redM.CombinationId);
    }

    [Fact]
    public async Task ACombinationOverridePriceWinsOverSpecialAndTierPrices()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        var detail = await f.VariantsAsync(id, ColorSize(overrideRedS: 20m));
        int Value(string name) => detail.Mappings.SelectMany(m => m.Values).Single(v => v.Name == name).Id;
        await Save(f, id, 8, null, null, Shop, new TierPrice(5, 7));

        var quote = (await Quote(f, id, 6, Value("Red"), Value("S"))).Value!;

        Assert.Equal((20m, 120m, PriceRule.VariantOverride), (quote.UnitPrice, quote.LineTotal, quote.AppliedRule));
        Assert.Null(quote.ComparePrice);
    }

    [Fact]
    public async Task APlainProductRefusesValuesThatAreNotItsOwn()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        var other = await f.ProductAsync(10);
        var detail = await f.VariantsAsync(other, ColorSize());
        var foreignValue = detail.Mappings[0].Values[0].Id;

        Assert.Contains("valueIds", (await Quote(f, id, 1, foreignValue)).Errors.Keys);
    }

    [Fact]
    public async Task AZeroDecimalCurrencyRoundsTheLineTotal()
    {
        var f = new Fixture(0).Init();
        var id = await f.ProductAsync(10);

        var quote = (await Quote(f, id, 3)).Value!;

        Assert.Equal((10m, 30m), (quote.UnitPrice, quote.LineTotal));
    }

    [Fact]
    public async Task APriceThatWouldNotBePositiveIsRefused()
    {
        var f = new Fixture().Init();
        var id = await f.ProductAsync(10);
        f.Attributes.Attributes.Add(new ProductAttributeSpec { Id = 3, Name = "Promo" });
        var detail = await f.VariantsAsync(id, new SaveVariantsCommand(
            [new VariantAttributeInput(3, true, [new VariantValueInput("Free", null, -9)])], [new VariantCombinationInput([0], null, 1, null)]));
        var value = detail.Mappings[0].Values[0].Id;

        Assert.True((await Quote(f, id, 1, value)).Succeeded);

        // The regular price falls afterwards (a stored state the validation of a new save would normally prevent).
        f.Products.Products.Single(p => p.Id == id).Price = 9;
        Assert.Contains("price", (await Quote(f, id, 1, value)).Errors.Keys);
    }
}
