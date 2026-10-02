using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ProductVariantsTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Seller = 10;
    private const int Color = 1;
    private const int Size = 2;

    private sealed class Fixture
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public FakeAttributeStore Attributes { get; } = new();
        public FakeSpecificationStore Specs { get; } = new();
        public FakeInventoryStore Inventory { get; }
        public RecordingAuditLog Audit { get; } = new();

        public Fixture()
        {
            Inventory = new FakeInventoryStore(Products, Attributes);
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Shop", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Other", Active = true });
            foreach (var (id, name) in new[] { (Color, "Color"), (Size, "Size"), (3, "Material"), (4, "Style") })
                Attributes.Attributes.Add(new ProductAttributeSpec { Id = id, Name = name });
            Specs.Options.Add(new SpecificationAttributeOption { Id = 7, SpecificationAttributeId = 70, Name = "Cotton" });
            Specs.Options.Add(new SpecificationAttributeOption { Id = 8, SpecificationAttributeId = 70, Name = "Wool" });
        }

        public VendorProductDetailsService Create() => new(
            Products, Vendors, Attributes, new ProductAttributeService(Attributes), Specs, Inventory, Audit, new TestClock());

        public ProductService CreateProducts() => new(
            Products, Inventory, new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors, new NoMembers(), new FakeMediaStore(), new FakeTaxonomy(), Audit,
            new RecordingEmailSender(), TestOptions.Email(false), NullLog<ProductService>.Instance, new TestClock());

        public async Task<int> DraftAsync(int vendorId = Shop, decimal price = 10)
        {
            var result = await CreateProducts().CreateForVendorAsync(vendorId,
                new SaveVendorProductCommand("Shirt", null, null, price, 0, 3, [1], []), Seller, CancellationToken.None);
            return result.Value!.Id;
        }
    }

    private static VariantAttributeInput Attribute(int id, params string[] values) =>
        new(id, true, values.Select(v => new VariantValueInput(v)).ToList());

    private static VariantCombinationInput Combo(int[] indexes, int stock = 1, string? sku = null, decimal? price = null) =>
        new(indexes, sku, stock, price);

    private static SaveVariantsCommand Variants(IReadOnlyList<VariantAttributeInput> attributes, params VariantCombinationInput[] combos) =>
        new(attributes, combos);

    private static Task<CatalogResult<ProductAttributeDetail>> SetAsync(VendorProductDetailsService s, int productId, SaveVariantsCommand c, int vendorId = Shop) =>
        s.SetVariantsAsync(vendorId, productId, c, Seller, CancellationToken.None);

    // ---- Variants ----

    [Fact]
    public async Task ValidVariantsAreStoredWithKeysThatPointAtTheCreatedValues()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();

        var result = await SetAsync(service, id, Variants(
            [Attribute(Color, "Red", "Blue"), Attribute(Size, "S", "M")],
            Combo([0, 0], 3, "RS"), Combo([0, 1], 4), Combo([1, 0], 5)));

        Assert.True(result.Succeeded);
        var detail = result.Value!;
        Assert.Equal(2, detail.Mappings.Count);
        Assert.Equal(3, detail.Combinations.Count);
        var red = detail.Mappings[0].Values.Single(v => v.Name == "Red").Id;
        var small = detail.Mappings[1].Values.Single(v => v.Name == "S").Id;
        var first = detail.Combinations.Single(c => c.Sku == "RS");
        Assert.Equal($"{{\"{detail.Mappings[0].Mapping.Id}\":{red},\"{detail.Mappings[1].Mapping.Id}\":{small}}}", first.AttributesJson);
        Assert.Contains(f.Audit.Entries, e => e.Event == "product.variants_changed");
    }

    [Fact]
    public async Task EmptyAttributesClearTheVariants()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        await SetAsync(service, id, Variants([Attribute(Color, "Red")], Combo([0])));

        var cleared = await SetAsync(service, id, Variants([]));

        Assert.True(cleared.Succeeded);
        Assert.Empty(cleared.Value!.Mappings);
        Assert.Empty(cleared.Value.Combinations);
    }

    [Fact]
    public async Task AttributesNeedCombinationsAndCombinationsNeedAttributes()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();

        Assert.Contains("combinations", (await SetAsync(service, id, Variants([Attribute(Color, "Red")]))).Errors.Keys);
        Assert.Contains("combinations", (await SetAsync(service, id, Variants([], Combo([])))).Errors.Keys);
    }

    [Fact]
    public async Task AttributeAndValueRulesAreEnforced()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();

        Task<CatalogResult<ProductAttributeDetail>> Try(params VariantAttributeInput[] attrs) =>
            SetAsync(service, id, Variants(attrs, Combo(new int[attrs.Length])));

        Assert.Contains("attributes", (await Try(Attribute(Color, "A"), Attribute(Size, "A"), Attribute(3, "A"), Attribute(4, "A"))).Errors.Keys);
        Assert.Contains("attributes", (await Try(Attribute(Color, "A"), Attribute(Color, "B"))).Errors.Keys);
        Assert.Contains("attributes", (await Try(Attribute(99, "A"))).Errors.Keys);
        Assert.Contains("attributes", (await Try(Attribute(Color))).Errors.Keys);
        Assert.Contains("attributes", (await Try(Attribute(Color, Enumerable.Range(0, 21).Select(i => "v" + i).ToArray()))).Errors.Keys);
        Assert.Contains("attributes", (await Try(Attribute(Color, "Red", "red"))).Errors.Keys);
        Assert.Contains("attributes", (await Try(Attribute(Color, " "))).Errors.Keys);
        Assert.Contains("attributes", (await Try(new VariantAttributeInput(Color, true, [new VariantValueInput("Red", "red")]))).Errors.Keys);
        Assert.True((await Try(new VariantAttributeInput(Color, true, [new VariantValueInput("Red", "#AA00ff")]))).Succeeded);
    }

    [Fact]
    public async Task CombinationRulesAreEnforced()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        var attrs = new[] { Attribute(Color, "Red", "Blue"), Attribute(Size, "S") };

        Task<CatalogResult<ProductAttributeDetail>> Try(params VariantCombinationInput[] combos) => SetAsync(service, id, Variants(attrs, combos));

        Assert.Contains("combinations", (await Try(Combo([0]))).Errors.Keys);
        Assert.Contains("combinations", (await Try(Combo([0, 1]))).Errors.Keys);
        Assert.Contains("combinations", (await Try(Combo([-1, 0]))).Errors.Keys);
        Assert.Contains("combinations", (await Try(Combo([0, 0]), Combo([0, 0]))).Errors.Keys);
        Assert.Contains("combinations", (await Try(Combo([0, 0], -1))).Errors.Keys);
        Assert.Contains("combinations", (await Try(Combo([0, 0], VariantLimits.MaxStock + 1))).Errors.Keys);
        Assert.Contains("combinations", (await Try(Combo([0, 0], 1, null, 0))).Errors.Keys);
        Assert.True((await Try(Combo([0, 0]), Combo([1, 0]))).Succeeded);
    }

    [Fact]
    public async Task AtMostOneHundredCombinations()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        var values = Enumerable.Range(0, 20).Select(i => "v" + i).ToArray();
        var attrs = new[] { Attribute(Color, values), Attribute(Size, values), Attribute(3, values) };
        var combos = Enumerable.Range(0, 101).Select(i => Combo([i % 20, i / 20, 0])).ToArray();

        Assert.Contains("combinations", (await SetAsync(service, id, Variants(attrs, combos))).Errors.Keys);
        Assert.True((await SetAsync(service, id, Variants(attrs, combos.Take(100).ToArray()))).Succeeded);
    }

    [Fact]
    public async Task PriceAdjustmentsMustKeepEveryCombinationAboveZero()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync(price: 10);
        var cheap = new VariantAttributeInput(Color, true, [new VariantValueInput("Sale", null, -10)]);

        Assert.Contains("combinations", (await SetAsync(service, id, Variants([cheap], Combo([0])))).Errors.Keys);
        // An explicit price replaces the base price and the adjustments.
        Assert.True((await SetAsync(service, id, Variants([cheap], Combo([0], 1, null, 5)))).Succeeded);
    }

    [Fact]
    public async Task CombinationSkusMustBeUniqueInTheShop()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        var attrs = new[] { Attribute(Color, "Red", "Blue") };
        f.Attributes.TakenSkus.Add("TAKEN");

        Assert.Contains("combinations", (await SetAsync(service, id, Variants(attrs, Combo([0], 1, "taken")))).Errors.Keys);
        Assert.Contains("combinations", (await SetAsync(service, id, Variants(attrs, Combo([0], 1, "A"), Combo([1], 1, "a")))).Errors.Keys);
        Assert.True((await SetAsync(service, id, Variants(attrs, Combo([0], 1, "A"), Combo([1], 1, "B")))).Succeeded);
    }

    [Fact]
    public async Task VariantsAreIsolatedPerShopAndBlockedForInactiveShops()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        var command = Variants([Attribute(Color, "Red")], Combo([0]));

        Assert.Equal(CatalogErrors.NotFound, (await SetAsync(service, id, command, OtherShop)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.GetVariantsAsync(OtherShop, id, CancellationToken.None)).ErrorCode);

        f.Vendors.Vendors.Single(v => v.Id == Shop).Active = false;
        Assert.Equal(CatalogErrors.Forbidden, (await SetAsync(service, id, command)).ErrorCode);
        // Reading is still allowed for an inactive shop.
        Assert.True((await service.GetVariantsAsync(Shop, id, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task VariantsCanBeEditedWhileHidden()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        await f.CreateProducts().HideAsync(id, "Reason", 1, CancellationToken.None);

        Assert.True((await SetAsync(service, id, Variants([Attribute(Color, "Red")], Combo([0])))).Succeeded);
    }

    // ---- Stock rule and public visibility ----

    [Fact]
    public async Task OnlyVisibleProductsArePublic()
    {
        var f = new Fixture();
        var products = f.CreateProducts();
        var id = await f.DraftAsync();
        var product = f.Products.Products.Single(p => p.Id == id);
        product.VendorActive = true;

        Assert.Null(await products.GetPublicProductAsync(id, CancellationToken.None));

        product.Status = ProductStatus.Live;
        Assert.NotNull(await products.GetPublicProductAsync(id, CancellationToken.None));

        product.AvailableStartUtc = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(await products.GetPublicProductAsync(id, CancellationToken.None));

        product.AvailableStartUtc = null;
        product.Status = ProductStatus.HiddenByAdmin;
        Assert.Null(await products.GetPublicProductAsync(id, CancellationToken.None));

        product.Status = ProductStatus.Live;
        product.VendorActive = false;
        Assert.Null(await products.GetPublicProductAsync(id, CancellationToken.None));
        Assert.Null(await products.GetPublicProductAsync(9999, CancellationToken.None));
    }

    // ---- Specifications ----

    [Fact]
    public async Task SpecificationOptionsReplaceOnlyOptionRows()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        f.Specs.ProductSpecs.Add(new ProductSpecificationMapping { ProductId = id, AttributeType = SpecificationAttributeType.CustomText, CustomValue = "x" });

        Assert.True((await service.SetSpecOptionsAsync(Shop, id, [7, 8, 7], Seller, CancellationToken.None)).Succeeded);
        Assert.True((await service.GetSpecOptionIdsAsync(Shop, id, CancellationToken.None)).Value!.SequenceEqual([7, 8]));

        Assert.True((await service.SetSpecOptionsAsync(Shop, id, [8], Seller, CancellationToken.None)).Succeeded);
        Assert.True((await service.GetSpecOptionIdsAsync(Shop, id, CancellationToken.None)).Value!.SequenceEqual([8]));
        Assert.Contains(f.Specs.ProductSpecs, s => s.AttributeType == SpecificationAttributeType.CustomText);
    }

    [Fact]
    public async Task SpecificationRulesAndIsolation()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();

        Assert.Contains("optionIds", (await service.SetSpecOptionsAsync(Shop, id, [999], Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("optionIds", (await service.SetSpecOptionsAsync(Shop, id, [0], Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("optionIds", (await service.SetSpecOptionsAsync(Shop, id, Enumerable.Range(1, 31).ToArray(), Seller, CancellationToken.None)).Errors.Keys);
        Assert.Equal(CatalogErrors.NotFound, (await service.SetSpecOptionsAsync(OtherShop, id, [], Seller, CancellationToken.None)).ErrorCode);
        Assert.True((await service.SetSpecOptionsAsync(Shop, id, null, Seller, CancellationToken.None)).Succeeded);

        f.Vendors.Vendors.Single(v => v.Id == Shop).Active = false;
        Assert.Equal(CatalogErrors.Forbidden, (await service.SetSpecOptionsAsync(Shop, id, [], Seller, CancellationToken.None)).ErrorCode);
    }

    // ---- Tags ----

    [Fact]
    public async Task TagsAreNormalizedAndExistingTagsAreReused()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        var other = await f.DraftAsync();

        var result = await service.SetTagsAsync(Shop, id, ["  Gift ", "gift", "SALE", " "], Seller, CancellationToken.None);

        Assert.True(result.Value!.SequenceEqual(["gift", "sale"]));
        await service.SetTagsAsync(Shop, other, ["gift"], Seller, CancellationToken.None);
        Assert.Equal(2, f.Specs.Tags.Count);
        Assert.True((await service.GetTagsAsync(Shop, id, CancellationToken.None)).Value!.SequenceEqual(["gift", "sale"]));
    }

    [Fact]
    public async Task TagLimitsAndIsolation()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();

        Assert.Contains("tagNames", (await service.SetTagsAsync(Shop, id, Enumerable.Range(0, 21).Select(i => "t" + i).ToArray(), Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("tagNames", (await service.SetTagsAsync(Shop, id, [new string('a', 101)], Seller, CancellationToken.None)).Errors.Keys);
        Assert.Equal(CatalogErrors.NotFound, (await service.SetTagsAsync(OtherShop, id, [], Seller, CancellationToken.None)).ErrorCode);
        Assert.True((await service.SetTagsAsync(Shop, id, null, Seller, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AuditHoldsCountsOnly()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.DraftAsync();
        await service.SetTagsAsync(Shop, id, ["secretword"], Seller, CancellationToken.None);
        await service.SetSpecOptionsAsync(Shop, id, [7], Seller, CancellationToken.None);

        foreach (var name in new[] { "product.tags_changed", "product.specs_changed" })
            Assert.DoesNotContain("secretword", f.Audit.Entries.Last(e => e.Event == name).Details?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task OptionCatalogListsDefinitionsWithOptions()
    {
        var catalog = await new Fixture().Create().GetOptionCatalogAsync(CancellationToken.None);

        Assert.Equal(4, catalog.Attributes.Count);
        Assert.Equal([7, 8], catalog.SpecAttributes.Single().Options.Select(o => o.Id));
    }
}
