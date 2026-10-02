using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ProductLifecycleTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Seller = 10;
    private const int Admin = 1;

    private sealed class Fixture
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public NoMembers Members { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public RecordingEmailSender Email { get; } = new();
        public bool EmailEnabled { get; set; }

        public Fixture()
        {
            Products.DefaultPicture = true;
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Shop", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Other", Active = true });
            Members.Members.Add(new VendorMember { CustomerId = 10, Email = "a@example.com" });
            Members.Members.Add(new VendorMember { CustomerId = 11, Email = "b@example.com" });
        }

        public ProductService Create() => new(
            Products, new FakeInventoryStore(Products), new FakePrimaryCurrency(), new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors, Members, new FakeMediaStore(), new FakeTaxonomy(), Audit,
            Email, TestOptions.Email(EmailEnabled), NullLog<ProductService>.Instance, new TestClock());

        /// <summary>Creates a draft with a category and a valid price.</summary>
        public static async Task<int> DraftAsync(ProductService service, int[]? categories = null) =>
            (await service.CreateForVendorAsync(Shop,
                new SaveVendorProductCommand("Mug", null, null, 10, 0, 3, categories ?? [1], []), Seller, CancellationToken.None)).Value!.Id;

        public Product Product(int id) => Products.Products.Single(p => p.Id == id);
    }

    // ---- Seller transitions ----

    [Fact]
    public async Task SellerProductsStartAsDraftsAndCanBePublishedAndStopped()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);

        Assert.Equal(ProductStatus.Draft, f.Product(id).Status);
        Assert.False(f.Product(id).Published);

        Assert.True((await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None)).Succeeded);
        Assert.True(f.Product(id).Published);
        Assert.True((await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Stopped, Seller, CancellationToken.None)).Succeeded);
        Assert.Equal(ProductStatus.Stopped, f.Product(id).Status);
        Assert.True((await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None)).Succeeded);

        Assert.Equal(["product.published", "product.stopped", "product.published"], f.Audit.Events.Where(e => e is "product.published" or "product.stopped").ToArray());
    }

    [Fact]
    public async Task PublishNeedsPriceAndCategoryAndOnlyLiveOrStoppedAreValidTargets()
    {
        var f = new Fixture();
        var service = f.Create();
        var noCategory = await Fixture.DraftAsync(service, categories: []);
        var free = await Fixture.DraftAsync(service);
        f.Product(free).Price = 0;

        Assert.Contains("categoryIds", (await service.SetStatusForVendorAsync(Shop, noCategory, ProductStatus.Live, Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("price", (await service.SetStatusForVendorAsync(Shop, free, ProductStatus.Live, Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("status", (await service.SetStatusForVendorAsync(Shop, free, ProductStatus.Draft, Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("status", (await service.SetStatusForVendorAsync(Shop, free, ProductStatus.HiddenByAdmin, Seller, CancellationToken.None)).Errors.Keys);
        Assert.Equal(ProductStatus.Draft, f.Product(noCategory).Status);
    }

    [Fact]
    public async Task StoppingADraftIsInvalidAndRepeatingAStatusIsHarmless()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);

        Assert.Equal(CatalogErrors.ProductInvalidTransition, (await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Stopped, Seller, CancellationToken.None)).ErrorCode);

        await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None);
        var events = f.Audit.Entries.Count;
        Assert.True((await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None)).Succeeded);
        Assert.Equal(events, f.Audit.Entries.Count);
    }

    [Fact]
    public async Task StatusActionsAreIsolatedPerShopAndBlockedForInactiveShops()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);

        Assert.Equal(CatalogErrors.NotFound, (await service.SetStatusForVendorAsync(OtherShop, id, ProductStatus.Live, Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.RequestReviewForVendorAsync(OtherShop, id, Seller, CancellationToken.None)).ErrorCode);

        f.Vendors.Vendors.Single(v => v.Id == Shop).Active = false;
        Assert.Equal(CatalogErrors.Forbidden, (await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task LiveProductsCannotLoseTheirLastCategory()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        SaveVendorProductCommand NoCategory() => new("Mug", null, null, 10, 0, 3, [], []);

        Assert.True((await service.UpdateForVendorAsync(Shop, id, NoCategory(), Seller, CancellationToken.None)).Succeeded);

        await service.UpdateForVendorAsync(Shop, id, new SaveVendorProductCommand("Mug", null, null, 10, 0, 3, [1], []), Seller, CancellationToken.None);
        await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None);
        Assert.Contains("categoryIds", (await service.UpdateForVendorAsync(Shop, id, NoCategory(), Seller, CancellationToken.None)).Errors.Keys);
        Assert.Equal([1], f.Products.CategoriesOf(id));
    }

    // ---- Moderation ----

    [Fact]
    public async Task HideRequiresAReasonAndRemembersThePreviousState()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None);

        Assert.Contains("reason", (await service.HideAsync(id, null, Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("reason", (await service.HideAsync(id, "  ", Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("reason", (await service.HideAsync(id, new string('x', 2001), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Equal(CatalogErrors.NotFound, (await service.HideAsync(999, "x", Admin, CancellationToken.None)).ErrorCode);

        Assert.True((await service.HideAsync(id, " Counterfeit ", Admin, CancellationToken.None)).Succeeded);
        var product = f.Product(id);
        Assert.Equal(ProductStatus.HiddenByAdmin, product.Status);
        Assert.Equal(ProductStatus.Live, product.StatusBeforeHidden);
        Assert.Equal("Counterfeit", product.HiddenReason);
        Assert.Equal(Admin, product.HiddenByCustomerId);
        Assert.False(product.Published);

        Assert.Equal(CatalogErrors.ProductAlreadyHidden, (await service.HideAsync(id, "again", Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task HiddenProductsCannotBeChangedByTheShopButCanBeEditedAndReviewed()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None);
        await service.HideAsync(id, "Bad", Admin, CancellationToken.None);

        Assert.Equal(CatalogErrors.ProductHiddenByAdmin, (await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.ProductHiddenByAdmin, (await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Stopped, Seller, CancellationToken.None)).ErrorCode);

        var edit = await service.UpdateForVendorAsync(Shop, id, new SaveVendorProductCommand("Fixed", null, null, 10, 0, 3, [1], []), Seller, CancellationToken.None);
        Assert.True(edit.Succeeded);
        Assert.Equal(ProductStatus.HiddenByAdmin, f.Product(id).Status);

        Assert.True((await service.RequestReviewForVendorAsync(Shop, id, Seller, CancellationToken.None)).Succeeded);
        Assert.NotNull(f.Product(id).ReviewRequestedOnUtc);
        Assert.Contains("product.review_requested", f.Audit.Events);
    }

    [Fact]
    public async Task ReviewRequestIsOnlyForHiddenProducts()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);

        Assert.Equal(CatalogErrors.ProductNotHidden, (await service.RequestReviewForVendorAsync(Shop, id, Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.ProductNotHidden, (await service.UnhideAsync(id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.UnhideAsync(999, Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task UnhideRestoresThePreviousStateAndClearsModerationFields()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None);
        await service.HideAsync(id, "Bad", Admin, CancellationToken.None);
        await service.RequestReviewForVendorAsync(Shop, id, Seller, CancellationToken.None);

        Assert.True((await service.UnhideAsync(id, Admin, CancellationToken.None)).Succeeded);

        var product = f.Product(id);
        Assert.Equal(ProductStatus.Live, product.Status);
        Assert.Null(product.HiddenReason);
        Assert.Null(product.StatusBeforeHidden);
        Assert.Null(product.ReviewRequestedOnUtc);
        Assert.Null(product.HiddenByCustomerId);
        Assert.Contains("product.unhidden", f.Audit.Events);
    }

    [Fact]
    public async Task UnhideOfAStoppedOrDraftProductDoesNotPutItOnSale()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        await service.HideAsync(id, "Bad", Admin, CancellationToken.None);

        await service.UnhideAsync(id, Admin, CancellationToken.None);

        Assert.Equal(ProductStatus.Draft, f.Product(id).Status);
    }

    [Fact]
    public async Task AdminPublishedFlagNeverChangesAHiddenProduct()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);

        UpdateProductCommand Update(bool published) =>
            new(id, "Mug", null, null, 10, 0, 3, published, null, false, 0, [1], []);

        Assert.True((await service.UpdateAsync(Update(true), Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(ProductStatus.Live, f.Product(id).Status);
        await service.UpdateAsync(Update(false), Admin, CancellationToken.None);
        Assert.Equal(ProductStatus.Stopped, f.Product(id).Status);
        await service.UpdateAsync(Update(false), Admin, CancellationToken.None);
        Assert.Equal(ProductStatus.Stopped, f.Product(id).Status);

        await service.HideAsync(id, "Bad", Admin, CancellationToken.None);
        await service.UpdateAsync(Update(true), Admin, CancellationToken.None);
        Assert.Equal(ProductStatus.HiddenByAdmin, f.Product(id).Status);
    }

    // ---- Email and audit ----

    [Fact]
    public async Task MembersAreEmailedOnHideAndUnhideWithTheReasonEncoded()
    {
        var f = new Fixture { EmailEnabled = true };
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);

        await service.HideAsync(id, "Uses <b>fake</b> photos", Admin, CancellationToken.None);

        Assert.Equal(["a@example.com", "b@example.com"], f.Email.Sent.Select(m => m.ToAddress).ToArray());
        Assert.All(f.Email.Sent, m => Assert.DoesNotContain("<b>", m.HtmlBody));
        Assert.All(f.Email.Sent, m => Assert.Contains("fake", m.HtmlBody));

        f.Email.Sent.Clear();
        await service.UnhideAsync(id, Admin, CancellationToken.None);
        Assert.Equal(2, f.Email.Sent.Count);
    }

    [Fact]
    public async Task EmailFailureOrDisabledEmailNeverBlocksModeration()
    {
        var failing = new Fixture { EmailEnabled = true };
        failing.Email.Throw = true;
        var service = failing.Create();
        var id = await Fixture.DraftAsync(service);
        Assert.True((await service.HideAsync(id, "Bad", Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(ProductStatus.HiddenByAdmin, failing.Product(id).Status);

        var disabled = new Fixture();
        var disabledService = disabled.Create();
        var otherId = await Fixture.DraftAsync(disabledService);
        await disabledService.HideAsync(otherId, "Bad", Admin, CancellationToken.None);
        Assert.Empty(disabled.Email.Sent);
    }

    [Fact]
    public async Task AuditDoesNotContainTheReasonText()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);

        await service.HideAsync(id, "Secret moderator wording", Admin, CancellationToken.None);

        var details = System.Text.Json.JsonSerializer.Serialize(f.Audit.Entries.Single(e => e.Event == "product.hidden").Details);
        Assert.DoesNotContain("Secret moderator wording", details);
        Assert.Contains("reasonProvided", details);
    }
}
