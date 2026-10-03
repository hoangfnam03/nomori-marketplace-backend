using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Customers;

namespace Nomori.Marketplace.Services.Tests;

public sealed class CustomerProfileServiceTests
{
    [Fact]
    public async Task UpdateNormalizesValuesAndAuditsChangedFields()
    {
        var store = new InMemoryProfileStore(new CustomerProfile
        {
            CustomerId = 7,
            Email = "customer@example.com",
            EmailVerified = true,
            Username = "customer"
        });
        var audit = new CapturingAuditLogService();
        var service = new CustomerProfileService(store, new FakeMediaStore(), audit, new FixedClock());

        var result = await service.UpdateAsync(new UpdateCustomerProfileCommand(
            7,
            " Mai ",
            " Nomori ",
            "FEMALE",
            new DateTime(1995, 4, 12),
            "+84 901 234 567"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Mai", store.Profile!.FirstName);
        Assert.Equal("Nomori", store.Profile.LastName);
        Assert.Equal("female", store.Profile.Gender);
        Assert.Equal("+84 901 234 567", store.Profile.Phone);
        Assert.Equal("customer.profile_updated", audit.EventName);
        Assert.Contains("firstName", audit.ChangedFields);
        Assert.Contains("gender", audit.ChangedFields);
    }

    [Fact]
    public async Task UpdateRejectsInvalidProfileValuesWithoutWriting()
    {
        var store = new InMemoryProfileStore(new CustomerProfile { CustomerId = 7, Email = "customer@example.com" });
        var audit = new CapturingAuditLogService();
        var service = new CustomerProfileService(store, new FakeMediaStore(), audit, new FixedClock());

        var result = await service.UpdateAsync(new UpdateCustomerProfileCommand(
            7,
            null,
            null,
            "unknown",
            new DateTime(1899, 12, 31),
            "abc"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("gender", result.Errors.Keys);
        Assert.Contains("dateOfBirth", result.Errors.Keys);
        Assert.Contains("phone", result.Errors.Keys);
        Assert.False(store.WasUpdated);
        Assert.Null(audit.EventName);
    }

    [Fact]
    public async Task AvatarMustBeTheCustomersOwnUploadAndCanBeRemoved()
    {
        var media = new FakeMediaStore();
        var own = await media.InsertAsync(Avatar(uploadedBy: 7), [1], CancellationToken.None);
        var someoneElses = await media.InsertAsync(Avatar(uploadedBy: 8), [1], CancellationToken.None);
        var logo = await media.InsertAsync(new Nomori.Marketplace.Core.Media.MediaAsset
            { Purpose = Nomori.Marketplace.Core.Media.MediaPurpose.VendorLogo, UploadedByCustomerId = 7 }, [1], CancellationToken.None);
        var store = new InMemoryProfileStore(new CustomerProfile { CustomerId = 7, Email = "customer@example.com" });
        var audit = new CapturingAuditLogService();
        var service = new CustomerProfileService(store, media, audit, new FixedClock());
        UpdateCustomerProfileCommand WithAvatar(int? id) => new(7, null, null, null, null, null, id);

        Assert.Contains("avatarPictureId", (await service.UpdateAsync(WithAvatar(someoneElses), CancellationToken.None)).Errors.Keys);
        Assert.Contains("avatarPictureId", (await service.UpdateAsync(WithAvatar(logo), CancellationToken.None)).Errors.Keys);
        Assert.Contains("avatarPictureId", (await service.UpdateAsync(WithAvatar(999), CancellationToken.None)).Errors.Keys);
        Assert.False(store.WasUpdated);

        Assert.True((await service.UpdateAsync(WithAvatar(own), CancellationToken.None)).Succeeded);
        Assert.Equal(own, store.Profile!.AvatarPictureId);
        Assert.Contains("avatar", audit.ChangedFields);

        // Null keeps the avatar; 0 removes it.
        Assert.True((await service.UpdateAsync(WithAvatar(null), CancellationToken.None)).Succeeded);
        Assert.Equal(own, store.Profile!.AvatarPictureId);
        Assert.True((await service.UpdateAsync(WithAvatar(0), CancellationToken.None)).Succeeded);
        Assert.Equal(0, store.Profile!.AvatarPictureId);
    }

    private static Nomori.Marketplace.Core.Media.MediaAsset Avatar(int uploadedBy) =>
        new() { Purpose = Nomori.Marketplace.Core.Media.MediaPurpose.CustomerAvatar, UploadedByCustomerId = uploadedBy };

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class InMemoryProfileStore(CustomerProfile profile) : ICustomerProfileStore
    {
        public CustomerProfile? Profile { get; private set; } = profile;
        public bool WasUpdated { get; private set; }

        public Task<CustomerProfile?> GetAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Profile?.CustomerId == customerId ? Profile : null);

        public Task UpdateAsync(CustomerProfile profile, CancellationToken cancellationToken)
        {
            Profile = profile;
            WasUpdated = true;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingAuditLogService : IAuditLogService
    {
        public string? EventName { get; private set; }
        public IReadOnlyList<string> ChangedFields { get; private set; } = [];

        public Task WriteAsync(string eventName, int? customerId = null, int? targetCustomerId = null,
            string? entityType = null, int? entityId = null, string? ipAddress = null, object? details = null,
            CancellationToken cancellationToken = default)
        {
            EventName = eventName;
            var changedFields = details?.GetType().GetProperty("changedFields")?.GetValue(details) as IEnumerable<string>;
            ChangedFields = changedFields?.ToArray() ?? [];
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditLog>> GetRecentAsync(int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditLog>>([]);
    }
}
