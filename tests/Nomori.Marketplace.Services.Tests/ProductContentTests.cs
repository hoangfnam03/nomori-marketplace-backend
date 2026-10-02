using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ProductContentTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Seller = 10;

    private sealed class Fixture
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture()
        {
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Shop", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Other", Active = true });
        }

        public ProductService Create() => new(
            Products, new FakeInventoryStore(Products), new FakePrimaryCurrency(), new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors, new NoMembers(), new FakeMediaStore(), new FakeTaxonomy(), Audit,
            new RecordingEmailSender(), TestOptions.Email(false), NullLog<ProductService>.Instance, new TestClock());
    }

    private static SaveVendorProductCommand Save(
        string name = "Mug", string? sku = null, string? gtin = null, string? description = null,
        DateTime? start = null, DateTime? end = null) =>
        new(name, null, description, 10, 0, 3, [1], [], sku, gtin, null, start, end);

    private static Task<CatalogResult<Product>> CreateAsync(ProductService service, SaveVendorProductCommand command, int vendorId = Shop) =>
        service.CreateForVendorAsync(vendorId, command, Seller, CancellationToken.None);

    // ---- HTML ----

    [Theory]
    [InlineData("<p>Hi</p><script>alert(1)</script>", "<p>Hi</p>")]
    [InlineData("<p onclick=\"x()\">Hi</p>", "<p>Hi</p>")]
    [InlineData("<img src=\"http://x/y.png\"><p>Hi</p>", "<p>Hi</p>")]
    [InlineData("<iframe src=\"http://x\"></iframe><p>Hi</p>", "<p>Hi</p>")]
    [InlineData("<p style=\"color:red\">Hi</p>", "<p>Hi</p>")]
    public void DangerousMarkupIsRemoved(string input, string expected) =>
        Assert.Equal(expected, HtmlContent.Sanitize(input));

    [Fact]
    public void FormattingIsKeptAndLinksAreSafe()
    {
        var clean = HtmlContent.Sanitize("<h2>T</h2><ul><li><strong>a</strong></li></ul><a href=\"https://nomori.vn\">ok</a><a href=\"javascript:alert(1)\">bad</a>")!;

        Assert.Contains("<h2>T</h2>", clean);
        Assert.Contains("<li><strong>a</strong></li>", clean);
        Assert.Contains("href=\"https://nomori.vn\"", clean);
        Assert.Contains("rel=\"noopener nofollow\"", clean);
        Assert.DoesNotContain("javascript", clean);
    }

    [Fact]
    public void BlankDescriptionBecomesNull()
    {
        Assert.Null(HtmlContent.Sanitize("   "));
        Assert.Null(HtmlContent.Sanitize("<script>x</script>"));
    }

    [Fact]
    public async Task SellerAndAdminSavesStoreTheSanitizedDescription()
    {
        var f = new Fixture();
        var service = f.Create();

        var seller = await CreateAsync(service, Save(description: "<p>a</p><script>x</script>"));
        Assert.Equal("<p>a</p>", seller.Value!.FullDescription);

        var admin = await service.CreateAsync(
            new CreateProductCommand("Admin", null, "<p onclick=\"x()\">b</p>", 5, 0, 1, true, Shop, false, 0, [1], []), 1, CancellationToken.None);
        Assert.Equal("<p>b</p>", admin.Value!.FullDescription);
    }

    [Fact]
    public async Task OverLongDescriptionIsRefused()
    {
        var service = new Fixture().Create();

        var result = await CreateAsync(service, Save(description: new string('a', HtmlContent.MaxLength + 1)));

        Assert.Contains("fullDescription", result.Errors.Keys);
    }

    // ---- SKU, GTIN, window ----

    [Fact]
    public async Task SkuIsUniqueInsideAShopIgnoringCase()
    {
        var f = new Fixture();
        var service = f.Create();
        var first = await CreateAsync(service, Save(sku: "ABC-1"));
        Assert.True(first.Succeeded);

        Assert.Contains("sku", (await CreateAsync(service, Save(sku: "abc-1"))).Errors.Keys);
        Assert.True((await CreateAsync(service, Save(sku: "ABC-1"), OtherShop)).Succeeded);

        // Keeping its own SKU on update is fine.
        var update = await service.UpdateForVendorAsync(Shop, first.Value!.Id, Save(sku: "ABC-1"), Seller, CancellationToken.None);
        Assert.True(update.Succeeded);
    }

    [Fact]
    public async Task ADeletedProductFreesItsSkuAndBlankBecomesNull()
    {
        var f = new Fixture();
        var service = f.Create();
        var first = await CreateAsync(service, Save(sku: "X1"));
        await service.DeleteForVendorAsync(Shop, first.Value!.Id, Seller, CancellationToken.None);

        Assert.True((await CreateAsync(service, Save(sku: "X1"))).Succeeded);
        Assert.Null((await CreateAsync(service, Save(sku: "  "))).Value!.Sku);
    }

    [Theory]
    [InlineData("12345678", true)]
    [InlineData("123456789012", true)]
    [InlineData("1234567890123", true)]
    [InlineData("12345678901234", true)]
    [InlineData("1234567", false)]
    [InlineData("12345678901", false)]
    [InlineData("12345678ABCD", false)]
    public async Task GtinMustBeDigitsOfAValidLength(string gtin, bool valid)
    {
        var result = await CreateAsync(new Fixture().Create(), Save(gtin: gtin));

        Assert.Equal(valid, result.Succeeded);
        if (!valid) Assert.Contains("gtin", result.Errors.Keys);
    }

    [Fact]
    public async Task TheWindowEndMustBeAfterTheStart()
    {
        var service = new Fixture().Create();
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Contains("availableEndUtc", (await CreateAsync(service, Save(start: start, end: start))).Errors.Keys);
        Assert.Contains("availableEndUtc", (await CreateAsync(service, Save(start: start, end: start.AddDays(-1)))).Errors.Keys);
        Assert.True((await CreateAsync(service, Save(start: start, end: start.AddDays(1)))).Succeeded);
        Assert.True((await CreateAsync(service, Save(start: start))).Succeeded);
    }

    [Fact]
    public void AvailabilityFollowsTheWindow()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(new Product().IsAvailableAt(now));
        Assert.False(new Product { AvailableStartUtc = now.AddDays(1) }.IsAvailableAt(now));
        Assert.True(new Product { AvailableStartUtc = now }.IsAvailableAt(now));
        Assert.True(new Product { AvailableStartUtc = now.AddDays(-1), AvailableEndUtc = now.AddDays(1) }.IsAvailableAt(now));
        Assert.False(new Product { AvailableEndUtc = now }.IsAvailableAt(now));
    }

    // ---- Related products ----

    private static async Task<int> LiveAsync(ProductService service, string name, int vendorId = Shop, DateTime? start = null)
    {
        var product = (await CreateAsync(service, Save(name, start: start), vendorId)).Value!;
        product.Status = ProductStatus.Live;
        product.VendorActive = true;
        return product.Id;
    }

    [Fact]
    public async Task RelatedProductsKeepOrderAndRejectInvalidIds()
    {
        var f = new Fixture();
        var service = f.Create();
        var main = await LiveAsync(service, "Main");
        var (a, b) = (await LiveAsync(service, "A"), await LiveAsync(service, "B"));
        var foreign = await LiveAsync(service, "Foreign", OtherShop);

        Task<CatalogResult<int[]>> Set(params int[] ids) => service.SetRelatedForVendorAsync(Shop, main, ids, Seller, CancellationToken.None);

        Assert.True((await Set(b, a)).Succeeded);
        Assert.Equal([b, a], (await service.GetDetailAsync(main, CancellationToken.None))!.RelatedProductIds);

        Assert.Contains("relatedProductIds", (await Set(main)).Errors.Keys);
        Assert.Contains("relatedProductIds", (await Set(a, a)).Errors.Keys);
        Assert.Contains("relatedProductIds", (await Set(foreign)).Errors.Keys);
        Assert.Contains("relatedProductIds", (await Set(9999)).Errors.Keys);
        Assert.True((await Set()).Succeeded);

        var tooMany = Enumerable.Range(0, 13).Select(_ => 1).ToArray();
        Assert.Contains("relatedProductIds", (await Set(tooMany)).Errors.Keys);
    }

    [Fact]
    public async Task OnlyVisibleRelatedProductsAreShownToCustomers()
    {
        var f = new Fixture();
        var service = f.Create();
        var main = await LiveAsync(service, "Main");
        var visible = await LiveAsync(service, "Visible");
        var stopped = await LiveAsync(service, "Stopped");
        var future = await LiveAsync(service, "Future", start: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var deleted = await LiveAsync(service, "Deleted");
        f.Products.Products.Single(p => p.Id == stopped).Status = ProductStatus.Stopped;
        Assert.True((await service.SetRelatedForVendorAsync(Shop, main, [future, stopped, deleted, visible], Seller, CancellationToken.None)).Succeeded);
        await service.DeleteForVendorAsync(Shop, deleted, Seller, CancellationToken.None);

        var shown = await service.GetVisibleRelatedAsync(main, CancellationToken.None);

        Assert.Equal([visible], shown.Select(p => p.Id));
    }

    [Fact]
    public async Task RelatedProductsAreIsolatedPerShopAndBlockedForInactiveShops()
    {
        var f = new Fixture();
        var service = f.Create();
        var main = await LiveAsync(service, "Main");

        var foreign = await service.SetRelatedForVendorAsync(OtherShop, main, [], Seller, CancellationToken.None);
        Assert.Equal(CatalogErrors.NotFound, foreign.ErrorCode);

        f.Vendors.Vendors.Single(v => v.Id == Shop).Active = false;
        var inactive = await service.SetRelatedForVendorAsync(Shop, main, [], Seller, CancellationToken.None);
        Assert.Equal(CatalogErrors.Forbidden, inactive.ErrorCode);
    }

    // ---- Copy ----

    [Fact]
    public async Task CopyMakesADraftWithoutSkuPicturesOrSchedule()
    {
        var f = new Fixture();
        var service = f.Create();
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var source = (await CreateAsync(service, new SaveVendorProductCommand("Mug", "short", "<p>full</p>", 10, 0, 3, [1, 2], [], "SKU-1", "12345678", "MPN", start, start.AddDays(5)))).Value!;
        source.Status = ProductStatus.Live;

        var result = await service.CopyForVendorAsync(Shop, source.Id, Seller, CancellationToken.None);

        Assert.True(result.Succeeded);
        var copy = result.Value!;
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Copy of Mug", copy.Name);
        Assert.Equal(ProductStatus.Draft, copy.Status);
        Assert.Equal(Shop, copy.VendorId);
        Assert.Equal("<p>full</p>", copy.FullDescription);
        Assert.Equal("12345678", copy.Gtin);
        Assert.Equal("MPN", copy.ManufacturerPartNumber);
        Assert.Null(copy.Sku);
        Assert.Null(copy.AvailableStartUtc);
        Assert.Null(copy.AvailableEndUtc);
        Assert.Equal([1, 2], f.Products.CategoriesOf(copy.Id));
        var detail = (await service.GetDetailAsync(copy.Id, CancellationToken.None))!;
        Assert.Empty(detail.PictureIds);
        Assert.Empty(detail.RelatedProductIds);
    }

    [Fact]
    public async Task CopyCutsALongNameAndWorksWhileHidden()
    {
        var f = new Fixture();
        var service = f.Create();
        var source = (await CreateAsync(service, Save(new string('n', 400)))).Value!;
        Assert.True((await service.HideAsync(source.Id, "Reason", 1, CancellationToken.None)).Succeeded);

        var result = await service.CopyForVendorAsync(Shop, source.Id, Seller, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(400, result.Value!.Name.Length);
        Assert.Equal(ProductStatus.Draft, result.Value.Status);
    }

    [Fact]
    public async Task CopyIsIsolatedPerShopAndBlockedForInactiveShops()
    {
        var f = new Fixture();
        var service = f.Create();
        var source = (await CreateAsync(service, Save())).Value!;

        Assert.Equal(CatalogErrors.NotFound, (await service.CopyForVendorAsync(OtherShop, source.Id, Seller, CancellationToken.None)).ErrorCode);

        f.Vendors.Vendors.Single(v => v.Id == Shop).Active = false;
        Assert.Equal(CatalogErrors.Forbidden, (await service.CopyForVendorAsync(Shop, source.Id, Seller, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task AuditHoldsIdsOnly()
    {
        var f = new Fixture();
        var service = f.Create();
        var main = await LiveAsync(service, "Secret name");
        var other = await LiveAsync(service, "Other");
        await service.SetRelatedForVendorAsync(Shop, main, [other], Seller, CancellationToken.None);
        await service.CopyForVendorAsync(Shop, main, Seller, CancellationToken.None);

        foreach (var name in new[] { "product.related_changed", "product.copied" })
        {
            var entry = f.Audit.Entries.Last(e => e.Event == name);
            Assert.DoesNotContain("Secret", entry.Details?.ToString() ?? string.Empty);
        }
    }
}
