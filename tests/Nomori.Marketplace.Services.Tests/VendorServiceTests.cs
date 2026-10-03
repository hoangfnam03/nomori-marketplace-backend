using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Vendors;

namespace Nomori.Marketplace.Services.Tests;

public sealed class VendorServiceTests
{
    [Fact]
    public async Task DeleteAuditsEveryFormerMemberAsRemovedByAdmin()
    {
        var store = new FakeVendorStore { FormerMembers = [10, 11] };
        var audit = new RecordingAuditLog();
        var service = new VendorService(store, new FakeMediaStore(), audit, new TestClock());

        store.Vendors.Add(new Vendor { Id = 5, Name = "Shop" });

        Assert.True((await service.DeleteAsync(5, 1, CancellationToken.None)).Succeeded);

        Assert.Equal(2, audit.Entries.Count(e => e.Event == "vendor.member_removed"));
        Assert.Equal([10, 11], audit.Entries.Select(e => e.TargetCustomerId!.Value).ToArray());
    }

    [Fact]
    public async Task DeleteReturnsFalseForUnknownVendor()
    {
        var store = new FakeVendorStore { FormerMembers = null };
        var audit = new RecordingAuditLog();

        var result = await new VendorService(store, new FakeMediaStore(), audit, new TestClock()).DeleteAsync(5, 1, CancellationToken.None);

        Assert.Equal(VendorErrors.NotFound, result.ErrorCode);
        Assert.Empty(audit.Entries);
    }

    private static readonly VendorCaller Admin = new(1, IsAdmin: true, MemberVendorId: null);
    private static readonly VendorCaller Member = new(10, IsAdmin: false, MemberVendorId: 5);
    private static readonly VendorCaller OtherMember = new(11, IsAdmin: false, MemberVendorId: 6);

    private static (VendorService Service, FakeVendorStore Store, RecordingAuditLog Audit) Create(params Vendor[] vendors)
    {
        var store = new FakeVendorStore();
        store.Vendors.AddRange(vendors);
        var audit = new RecordingAuditLog();
        return (new VendorService(store, new FakeMediaStore(), audit, new TestClock()), store, audit);
    }

    private static Vendor Shop() => new()
    {
        Id = 5, Name = "Old", Email = "old@example.com", PhoneNumber = "0901", Active = true, DisplayOrder = 3, AdminComment = "note"
    };

    private static UpdateVendorCommand Profile(string name = "Shop", string email = "shop@example.com", string? phone = "0901 234 567") =>
        new(5, name, email, phone, "About us", "0123", "Hanoi");

    [Fact]
    public async Task UpdateValidatesAndNormalizes()
    {
        var (service, _, _) = Create(Shop());

        var invalid = await service.UpdateAsync(new UpdateVendorCommand(5, "", "", null, null, null, null), Admin, CancellationToken.None);
        Assert.Contains("name", invalid.Errors.Keys);
        Assert.Contains("email", invalid.Errors.Keys);
        Assert.DoesNotContain("phoneNumber", invalid.Errors.Keys);

        var ok = await service.UpdateAsync(new UpdateVendorCommand(5, " New ", " NEW@Example.com ", null, " ", null, null, Active: true, DisplayOrder: 2), Admin, CancellationToken.None);
        Assert.Equal("New", ok.Value!.Name);
        Assert.Equal("new@example.com", ok.Value.Email);
        Assert.Null(ok.Value.Description);
        Assert.Equal(2, ok.Value.DisplayOrder);

        var missing = await service.UpdateAsync(Profile() with { Id = 99 }, Admin, CancellationToken.None);
        Assert.Equal(VendorErrors.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task MemberEditsOwnShopProfileButNotAdminFields()
    {
        var (service, store, audit) = Create(Shop());

        var result = await service.UpdateAsync(Profile(), Member, CancellationToken.None);

        Assert.True(result.Succeeded);
        var shop = store.Vendors[0];
        Assert.Equal(("Shop", "shop@example.com", "0901 234 567", "About us", "0123", "Hanoi"),
            (shop.Name, shop.Email, shop.PhoneNumber, shop.Description, shop.TaxCode, shop.BusinessAddress));
        // Fields the member did not send keep their values.
        Assert.Equal((true, 3, "note"), (shop.Active, shop.DisplayOrder, shop.AdminComment));
        Assert.Contains("vendor.updated", audit.Events);

        Assert.Equal(VendorErrors.Forbidden, (await service.UpdateAsync(Profile() with { Active = false }, Member, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.Forbidden, (await service.UpdateAsync(Profile() with { DisplayOrder = 0 }, Member, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.Forbidden, (await service.UpdateAsync(Profile() with { AdminComment = "x" }, Member, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task OnlyMembersOfTheShopAndAdminsMayEdit()
    {
        var (service, _, _) = Create(Shop());

        Assert.Equal(VendorErrors.NotFound, (await service.UpdateAsync(Profile(), OtherMember, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.NotFound, (await service.UpdateAsync(Profile(), new VendorCaller(12, false, null), CancellationToken.None)).ErrorCode);
        Assert.True((await service.UpdateAsync(Profile(), Admin, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task MembersCannotEditAnInactiveShop()
    {
        var shop = Shop();
        shop.Active = false;
        var (service, _, _) = Create(shop);

        Assert.Equal(VendorErrors.Inactive, (await service.UpdateAsync(Profile(), Member, CancellationToken.None)).ErrorCode);
        Assert.True((await service.UpdateAsync(Profile(), Admin, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task MemberMustGiveAValidPhoneNumber()
    {
        var (service, _, _) = Create(Shop());

        Assert.Contains("phoneNumber", (await service.UpdateAsync(Profile(phone: null), Member, CancellationToken.None)).Errors.Keys);
        Assert.Contains("phoneNumber", (await service.UpdateAsync(Profile(phone: "call me"), Member, CancellationToken.None)).Errors.Keys);
        Assert.Contains("email", (await service.UpdateAsync(Profile(email: "not-an-email"), Member, CancellationToken.None)).Errors.Keys);
    }

    [Fact]
    public async Task ShopNamesAreUniqueIgnoringCase()
    {
        var (service, _, _) = Create(Shop(), new Vendor { Id = 6, Name = "Meo Meo Shop", Email = "b@example.com", Active = true });

        Assert.Contains("name", (await service.UpdateAsync(Profile(name: "  meo meo shop "), Member, CancellationToken.None)).Errors.Keys);
        // Keeping its own name is fine.
        Assert.True((await service.UpdateAsync(Profile(name: "old"), Member, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task SavingWithoutChangesWritesNothing()
    {
        var (service, store, audit) = Create(Shop());
        await service.UpdateAsync(Profile(), Member, CancellationToken.None);
        var writes = store.UpdateCount;
        var audits = audit.Entries.Count;

        Assert.True((await service.UpdateAsync(Profile(), Member, CancellationToken.None)).Succeeded);

        Assert.Equal(writes, store.UpdateCount);
        Assert.Equal(audits, audit.Entries.Count);
    }
}
