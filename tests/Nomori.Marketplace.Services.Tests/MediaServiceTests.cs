using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Media;

namespace Nomori.Marketplace.Services.Tests;

public sealed class MediaServiceTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10];
    private static readonly byte[] Gif = "GIF89a...."u8.ToArray();
    private static readonly byte[] Webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

    private static readonly MediaCaller CatalogAdmin = new(1, CanManageCatalog: true, CanManageVendors: false, MemberVendorId: null);
    private static readonly MediaCaller VendorAdmin = new(2, false, CanManageVendors: true, null);
    private static readonly MediaCaller ShopMember = new(10, false, false, MemberVendorId: 5);
    private static readonly MediaCaller OtherShopMember = new(11, false, false, MemberVendorId: 6);
    private static readonly MediaCaller Customer = new(12, false, false, null);

    private sealed class Fixture
    {
        public FakeMediaStore Store { get; } = new();
        public FakeVendorStore Vendors { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public int MaxBytes { get; set; } = 1024;

        public Fixture() => Vendors.Vendors.Add(new Vendor { Id = 5, Name = "Shop" });

        public MediaService Create() => new(Store, Vendors, Audit, new TestClock(), Options.Create(new MediaOptions { MaxUploadBytes = MaxBytes }));
    }

    [Theory]
    [MemberData(nameof(Signatures))]
    public void SignatureDetectsSupportedFormats(byte[] data, string? expected) =>
        Assert.Equal(expected, ImageSignature.Detect(data));

    public static TheoryData<byte[], string?> Signatures => new()
    {
        { Png, "image/png" },
        { Jpeg, "image/jpeg" },
        { Gif, "image/gif" },
        { Webp, "image/webp" },
        { "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), null },
        { "<html><script>alert(1)</script></html>"u8.ToArray(), null },
        { "plain text pretending to be a.png"u8.ToArray(), null },
        { new byte[] { 0x89, 0x50 }, null },
        { Array.Empty<byte>(), null }
    };

    [Fact]
    public async Task UploadStoresSniffedTypeHashAndAudits()
    {
        var f = new Fixture();
        var result = await f.Create().UploadAsync(new UploadMediaCommand(MediaPurpose.Category, null, Png), CatalogAdmin, CancellationToken.None);

        Assert.True(result.Succeeded);
        var asset = f.Store.Assets[result.Value!.Id];
        Assert.Equal("image/png", asset.MimeType);
        Assert.Equal(64, asset.Sha256.Length);
        Assert.Equal(CatalogAdmin.CustomerId, asset.UploadedByCustomerId);
        Assert.Null(asset.VendorId);
        Assert.Contains("media.uploaded", f.Audit.Events);
    }

    [Fact]
    public async Task UploadRejectsBadFilesAndPurposes()
    {
        var f = new Fixture();
        var service = f.Create();

        Assert.Contains("file", (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Category, null, []), CatalogAdmin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("file", (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Category, null, new byte[2000]), CatalogAdmin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("file", (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Category, null, "<svg/>"u8.ToArray()), CatalogAdmin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("purpose", (await service.UploadAsync(new UploadMediaCommand(null, null, Png), CatalogAdmin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("purpose", (await service.UploadAsync(new UploadMediaCommand((MediaPurpose)99, null, Png), CatalogAdmin, CancellationToken.None)).Errors.Keys);
        Assert.Empty(f.Store.Assets);
    }

    [Fact]
    public async Task CategoryAndManufacturerUploadsNeedCatalogPermission()
    {
        var service = new Fixture().Create();

        foreach (var purpose in new[] { MediaPurpose.Category, MediaPurpose.Manufacturer })
        {
            Assert.True((await service.UploadAsync(new UploadMediaCommand(purpose, null, Png), CatalogAdmin, CancellationToken.None)).Succeeded);
            Assert.Equal(MediaErrors.Forbidden, (await service.UploadAsync(new UploadMediaCommand(purpose, null, Png), ShopMember, CancellationToken.None)).ErrorCode);
            Assert.Equal(MediaErrors.Forbidden, (await service.UploadAsync(new UploadMediaCommand(purpose, null, Png), Customer, CancellationToken.None)).ErrorCode);
        }

        Assert.Contains("vendorId", (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Category, 5, Png), CatalogAdmin, CancellationToken.None)).Errors.Keys);
    }

    [Fact]
    public async Task ProductUploadsAreForMembersOfTheirOwnShopOnly()
    {
        var f = new Fixture();
        var service = f.Create();

        var ok = await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Product, null, Jpeg), ShopMember, CancellationToken.None);
        Assert.Equal(5, ok.Value!.VendorId);

        Assert.Equal(MediaErrors.Forbidden, (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Product, 6, Jpeg), ShopMember, CancellationToken.None)).ErrorCode);
        Assert.Equal(MediaErrors.Forbidden, (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Product, 5, Jpeg), VendorAdmin, CancellationToken.None)).ErrorCode);
        Assert.Equal(MediaErrors.Forbidden, (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Product, null, Jpeg), Customer, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task VendorLogoUsesOwnShopOrAdminMustNameOne()
    {
        var f = new Fixture();
        var service = f.Create();

        Assert.Equal(5, (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.VendorLogo, null, Gif), ShopMember, CancellationToken.None)).Value!.VendorId);
        Assert.Equal(MediaErrors.Forbidden, (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.VendorLogo, 5, Gif), OtherShopMember, CancellationToken.None)).ErrorCode);
        Assert.Equal(MediaErrors.Forbidden, (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.VendorLogo, null, Gif), Customer, CancellationToken.None)).ErrorCode);

        Assert.Contains("vendorId", (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.VendorLogo, null, Gif), VendorAdmin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("vendorId", (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.VendorLogo, 99, Gif), VendorAdmin, CancellationToken.None)).Errors.Keys);
        Assert.Equal(5, (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.VendorLogo, 5, Gif), VendorAdmin, CancellationToken.None)).Value!.VendorId);
    }

    [Fact]
    public async Task DeleteRespectsOwnershipAndUsage()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Product, null, Webp), ShopMember, CancellationToken.None)).Value!.Id;

        Assert.Equal(MediaErrors.NotFound, (await service.DeleteAsync(id, OtherShopMember, CancellationToken.None)).ErrorCode);
        Assert.Equal(MediaErrors.NotFound, (await service.DeleteAsync(id, Customer, CancellationToken.None)).ErrorCode);
        Assert.Equal(MediaErrors.NotFound, (await service.DeleteAsync(id, CatalogAdmin, CancellationToken.None)).ErrorCode);

        f.Store.Referenced.Add(id);
        Assert.Equal(MediaErrors.InUse, (await service.DeleteAsync(id, ShopMember, CancellationToken.None)).ErrorCode);

        f.Store.Referenced.Clear();
        var teammate = new MediaCaller(13, false, false, 5);
        Assert.True((await service.DeleteAsync(id, teammate, CancellationToken.None)).Succeeded);
        Assert.Contains("media.deleted", f.Audit.Events);
        Assert.Equal(MediaErrors.NotFound, (await service.DeleteAsync(id, ShopMember, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task AdminsCanDeleteAssetsOfTheirArea()
    {
        var f = new Fixture();
        var service = f.Create();
        var categoryImage = (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Category, null, Png), new MediaCaller(99, true, false, null), CancellationToken.None)).Value!.Id;
        var shopImage = (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Product, null, Png), ShopMember, CancellationToken.None)).Value!.Id;

        Assert.True((await service.DeleteAsync(categoryImage, CatalogAdmin, CancellationToken.None)).Succeeded);
        Assert.True((await service.DeleteAsync(shopImage, VendorAdmin, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AttachmentAcceptsNoneAndMatchingPurposeOnly()
    {
        var f = new Fixture();
        var service = f.Create();
        var category = (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.Category, null, Png), CatalogAdmin, CancellationToken.None)).Value!.Id;
        var logo = (await service.UploadAsync(new UploadMediaCommand(MediaPurpose.VendorLogo, null, Png), ShopMember, CancellationToken.None)).Value!.Id;

        async Task<bool> Valid(int pictureId, MediaPurpose purpose, int? vendorId)
        {
            var errors = new Dictionary<string, string[]>();
            await MediaAttachment.ValidateAsync(f.Store, pictureId, purpose, vendorId, errors, CancellationToken.None);
            return errors.Count == 0;
        }

        Assert.True(await Valid(0, MediaPurpose.Category, null));
        Assert.True(await Valid(category, MediaPurpose.Category, null));
        Assert.False(await Valid(category, MediaPurpose.Manufacturer, null));
        Assert.False(await Valid(999, MediaPurpose.Category, null));
        Assert.False(await Valid(-1, MediaPurpose.Category, null));
        Assert.True(await Valid(logo, MediaPurpose.VendorLogo, 5));
        Assert.False(await Valid(logo, MediaPurpose.VendorLogo, 6));
    }
}
