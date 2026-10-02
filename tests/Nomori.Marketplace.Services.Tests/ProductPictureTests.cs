using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ProductPictureTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Seller = 10;

    private sealed class Fixture
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public FakeMediaStore Media { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture()
        {
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Shop", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Other", Active = true });
        }

        public ProductService Create() => new(
            Products, new FakeInventoryStore(Products), new FakePrimaryCurrency(), new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors, new NoMembers(), Media, new FakeTaxonomy(), Audit,
            new RecordingEmailSender(), TestOptions.Email(false), NullLog<ProductService>.Instance, new TestClock());

        public static async Task<int> DraftAsync(ProductService service) =>
            (await service.CreateForVendorAsync(Shop,
                new SaveVendorProductCommand("Mug", null, null, 10, 0, 3, [1], []), Seller, CancellationToken.None)).Value!.Id;

        /// <summary>An image uploaded for the product purpose by a shop.</summary>
        public int Picture(int vendorId = Shop, MediaPurpose purpose = MediaPurpose.Product)
        {
            var id = Media.Assets.Count + 1;
            Media.Assets[id] = new MediaAsset { Id = id, Purpose = purpose, VendorId = vendorId };
            return id;
        }
    }

    private static Task<CatalogResult<int[]>> SetAsync(ProductService service, int productId, params int[] ids) =>
        service.SetPicturesForVendorAsync(Shop, productId, ids, Seller, CancellationToken.None);

    [Fact]
    public async Task PicturesKeepTheirOrderAndTheFirstIsMain()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        var (a, b) = (f.Picture(), f.Picture());

        Assert.True((await SetAsync(service, id, a, b)).Succeeded);
        Assert.Equal([a, b], (await service.GetDetailAsync(id, CancellationToken.None))!.PictureIds);
        Assert.Equal(a, (await service.GetMainPictureIdsAsync([id], CancellationToken.None))[id]);

        Assert.True((await SetAsync(service, id, b, a)).Succeeded);
        Assert.Equal(b, (await service.GetMainPictureIdsAsync([id], CancellationToken.None))[id]);
    }

    [Fact]
    public async Task AtMostTenDistinctPicturesAreAccepted()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        var ids = Enumerable.Range(0, 11).Select(_ => f.Picture()).ToArray();

        Assert.True((await SetAsync(service, id, ids.Take(10).ToArray())).Succeeded);
        Assert.Contains("pictureIds", (await SetAsync(service, id, ids)).Errors.Keys);
        Assert.Contains("pictureIds", (await SetAsync(service, id, ids[0], ids[0])).Errors.Keys);
    }

    [Fact]
    public async Task OnlyProductPicturesOfTheSameShopThatAreFreeCanBeUsed()
    {
        var f = new Fixture();
        var service = f.Create();
        var first = await Fixture.DraftAsync(service);
        var second = await Fixture.DraftAsync(service);

        Assert.Contains("pictureIds", (await SetAsync(service, first, 999)).Errors.Keys);
        Assert.Contains("pictureIds", (await SetAsync(service, first, f.Picture(purpose: MediaPurpose.Category))).Errors.Keys);
        Assert.Contains("pictureIds", (await SetAsync(service, first, f.Picture(OtherShop))).Errors.Keys);

        var mine = f.Picture();
        Assert.True((await SetAsync(service, first, mine)).Succeeded);
        Assert.Contains("pictureIds", (await SetAsync(service, second, mine)).Errors.Keys);
        // Saving the same list again for the same product is fine.
        Assert.True((await SetAsync(service, first, mine)).Succeeded);
    }

    [Fact]
    public async Task PublishNeedsAPictureAndALiveProductKeepsOne()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);

        var refused = await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None);
        Assert.Contains("pictureIds", refused.Errors.Keys);

        Assert.True((await SetAsync(service, id, f.Picture())).Succeeded);
        Assert.True((await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Live, Seller, CancellationToken.None)).Succeeded);

        Assert.Contains("pictureIds", (await SetAsync(service, id)).Errors.Keys);
        Assert.True((await service.SetStatusForVendorAsync(Shop, id, ProductStatus.Stopped, Seller, CancellationToken.None)).Succeeded);
        Assert.True((await SetAsync(service, id)).Succeeded);
    }

    [Fact]
    public async Task PicturesAreIsolatedPerShopAndBlockedForInactiveShops()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        var picture = f.Picture(OtherShop);

        var foreign = await service.SetPicturesForVendorAsync(OtherShop, id, [picture], Seller, CancellationToken.None);
        Assert.Equal(CatalogErrors.NotFound, foreign.ErrorCode);

        f.Vendors.Vendors.Single(v => v.Id == Shop).Active = false;
        Assert.Equal(CatalogErrors.Forbidden, (await SetAsync(service, id)).ErrorCode);
    }

    [Fact]
    public async Task PicturesCanBeChangedWhileHiddenAndAuditHoldsIdsOnly()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await Fixture.DraftAsync(service);
        Assert.True((await SetAsync(service, id, f.Picture())).Succeeded);
        Assert.True((await service.HideAsync(id, "Violates policy", 1, CancellationToken.None)).Succeeded);

        Assert.True((await SetAsync(service, id, f.Picture())).Succeeded);

        var entry = f.Audit.Entries.Last(e => e.Event == "product.pictures_changed");
        var text = entry.Details!.ToString()!;
        Assert.Contains("count = 1", text);
        Assert.DoesNotContain("Violates", text);
    }
}
