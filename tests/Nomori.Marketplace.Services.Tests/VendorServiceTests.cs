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

    [Fact]
    public async Task UpdateValidatesAndNormalizes()
    {
        var store = new FakeVendorStore();
        store.Vendors.Add(new Vendor { Id = 5, Name = "Old", Email = "old@example.com" });
        var service = new VendorService(store, new FakeMediaStore(), new RecordingAuditLog(), new TestClock());

        var invalid = await service.UpdateAsync(new UpdateVendorCommand(5, "", "", null, null, true, 0), CancellationToken.None);
        Assert.Contains("name", invalid.Errors.Keys);
        Assert.Contains("email", invalid.Errors.Keys);

        var ok = await service.UpdateAsync(new UpdateVendorCommand(5, " New ", " NEW@Example.com ", " ", null, true, 2), CancellationToken.None);
        Assert.Equal("New", ok.Value!.Name);
        Assert.Equal("new@example.com", ok.Value.Email);
        Assert.Null(ok.Value.Description);

        var missing = await service.UpdateAsync(new UpdateVendorCommand(99, "x", "x@example.com", null, null, true, 0), CancellationToken.None);
        Assert.Equal(VendorErrors.NotFound, missing.ErrorCode);
    }
}
