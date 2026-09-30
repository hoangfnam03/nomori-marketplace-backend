using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Authentication;
using Nomori.Marketplace.Services.Vendors;

namespace Nomori.Marketplace.Services.Tests;

public sealed class VendorMemberServiceTests
{
    private const int VendorId = 5;
    private static readonly VendorCaller Member = new(10, false, VendorId);
    private static readonly VendorCaller OtherShopMember = new(50, false, 6);
    private static readonly VendorCaller Admin = new(1, true, null);

    private sealed class Fixture
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeMemberStore Members { get; } = new();
        public RecordingEmailSender Email { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public bool EmailEnabled { get; set; }
        public int MaxMembers { get; set; } = 20;

        public Fixture()
        {
            Vendors.Vendors.Add(new Vendor { Id = VendorId, Name = "Mai <Ceramics>", Active = true });
            Members.Add(10, "owner@example.com", lastLogin: DateTime.UtcNow);
        }

        public VendorMemberService Create() => new(
            Vendors, Members, new PasswordHasher(), Email, Audit, new TestClock(),
            Options.Create(new VendorOptions { MaxMembersPerVendor = MaxMembers }),
            Options.Create(new SecurityOptions()),
            TestOptions.Email(EmailEnabled),
            NullLog<VendorMemberService>.Instance);
    }

    [Fact]
    public async Task CreateTakesVendorFromCallerSendsEmailAndAudits()
    {
        var f = new Fixture { EmailEnabled = true };

        var result = await f.Create().CreateAsync(VendorId, new CreateVendorMemberCommand(" New@Example.com ", "Lan", null), Member, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(VendorMemberStatus.PendingSetup, result.Value!.Member.Status);
        Assert.Equal(VendorId, f.Members.LastCreateVendorId);
        Assert.Equal("new@example.com", f.Members.Find("new@example.com")!.Email);
        Assert.Equal(72, (f.Members.LastTokenExpiry!.Value - new TestClock().UtcNow).TotalHours);
        var mail = Assert.Single(f.Email.Sent);
        Assert.Contains("setup=1", mail.HtmlBody);
        Assert.DoesNotContain("<Ceramics>", mail.HtmlBody);
        Assert.Contains("vendor.member_created", f.Audit.Events);
    }

    [Fact]
    public async Task CreateRejectsExistingEmailFullShopAndInvalidInput()
    {
        var f = new Fixture();
        var service = f.Create();

        var exists = await service.CreateAsync(VendorId, new CreateVendorMemberCommand("owner@example.com", null, null), Member, CancellationToken.None);
        Assert.Equal(VendorErrors.MemberEmailAlreadyExists, exists.ErrorCode);

        f.MaxMembers = 1;
        var full = await f.Create().CreateAsync(VendorId, new CreateVendorMemberCommand("x@example.com", null, null), Member, CancellationToken.None);
        Assert.Equal(VendorErrors.MemberLimitReached, full.ErrorCode);

        var invalid = await service.CreateAsync(VendorId, new CreateVendorMemberCommand("bad", new string('a', 101), null), Member, CancellationToken.None);
        Assert.Contains("email", invalid.Errors.Keys);
        Assert.Contains("firstName", invalid.Errors.Keys);
    }

    [Fact]
    public async Task CreateIsForMembersOnlyAndHidesOtherShops()
    {
        var f = new Fixture();
        var service = f.Create();
        var command = new CreateVendorMemberCommand("x@example.com", null, null);

        Assert.Equal(VendorErrors.Forbidden, (await service.CreateAsync(VendorId, command, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.NotFound, (await service.CreateAsync(VendorId, command, OtherShopMember, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.NotFound, (await service.CreateAsync(VendorId, command, VendorCaller.Anonymous, CancellationToken.None)).ErrorCode);

        f.Vendors.Vendors[0].Active = false;
        Assert.Equal(VendorErrors.Forbidden, (await service.CreateAsync(VendorId, command, Member, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ListIsVisibleToMembersAndAdminsOnly()
    {
        var f = new Fixture();
        var service = f.Create();

        Assert.True((await service.ListAsync(VendorId, Member, CancellationToken.None)).Succeeded);
        Assert.True((await service.ListAsync(VendorId, Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(VendorErrors.NotFound, (await service.ListAsync(VendorId, OtherShopMember, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ResendReplacesTokenOnlyForPendingMembersOfTheSameShop()
    {
        var f = new Fixture { EmailEnabled = true };
        f.Members.Add(20, "pending@example.com", lastLogin: null);
        var service = f.Create();

        var resent = await service.ResendSetupEmailAsync(VendorId, 20, Member, CancellationToken.None);
        Assert.True(resent.Succeeded);
        Assert.Equal(1, f.Members.TokenReplacements);
        Assert.Single(f.Email.Sent);
        Assert.Contains("vendor.member_setup_resent", f.Audit.Events);

        Assert.Equal(VendorErrors.MemberAlreadyActive, (await service.ResendSetupEmailAsync(VendorId, 10, Member, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.NotFound, (await service.ResendSetupEmailAsync(VendorId, 999, Member, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.Forbidden, (await service.ResendSetupEmailAsync(VendorId, 20, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.NotFound, (await service.ResendSetupEmailAsync(VendorId, 20, OtherShopMember, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task EmailFailureDoesNotFailCreation()
    {
        var f = new Fixture { EmailEnabled = true };
        f.Email.Throw = true;

        var result = await f.Create().CreateAsync(VendorId, new CreateVendorMemberCommand("x@example.com", null, null), Member, CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RemoveAuditsSelfAndAdminRemovalsAndProtectsTheLastMember()
    {
        var f = new Fixture();
        f.Members.Add(20, "second@example.com", lastLogin: DateTime.UtcNow);
        var service = f.Create();

        Assert.Equal(VendorErrors.NotFound, (await service.RemoveAsync(VendorId, 999, Member, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.NotFound, (await service.RemoveAsync(VendorId, 20, OtherShopMember, CancellationToken.None)).ErrorCode);

        Assert.True((await service.RemoveAsync(VendorId, 20, Admin, CancellationToken.None)).Succeeded);
        var adminEntry = f.Audit.Entries.Single(e => e.Event == "vendor.member_removed").Details!;
        Assert.True((bool)adminEntry.GetType().GetProperty("byAdmin")!.GetValue(adminEntry)!);

        // Only member 10 is left: neither a member leaving nor an administrator may empty the shop.
        Assert.Equal(VendorErrors.MemberLastMember, (await service.RemoveAsync(VendorId, 10, Member, CancellationToken.None)).ErrorCode);
        Assert.Equal(VendorErrors.MemberLastMember, (await service.RemoveAsync(VendorId, 10, Admin, CancellationToken.None)).ErrorCode);
        Assert.Single(f.Audit.Entries, e => e.Event == "vendor.member_removed");
    }

    private sealed class FakeMemberStore : IVendorMemberStore
    {
        private readonly List<VendorMember> members = [];
        private int nextId = 100;

        public int? LastCreateVendorId { get; private set; }
        public DateTime? LastTokenExpiry { get; private set; }
        public int TokenReplacements { get; private set; }

        public void Add(int id, string email, DateTime? lastLogin) =>
            members.Add(new VendorMember { CustomerId = id, Email = email, LastLoginDateUtc = lastLogin, CreatedOnUtc = new TestClock().UtcNow });

        public VendorMember? Find(string email) => members.FirstOrDefault(m => m.Email == email);

        public Task<IReadOnlyList<VendorMember>> ListAsync(int vendorId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VendorMember>>(members.ToList());

        public Task<VendorMember?> GetAsync(int vendorId, int customerId, CancellationToken cancellationToken) =>
            Task.FromResult(members.FirstOrDefault(m => m.CustomerId == customerId));

        public Task<CreateMemberStoreResult> CreateAsync(int vendorId, string email, string? firstName, string? lastName,
            string passwordHash, string passwordSalt, string setupTokenHash, DateTime setupTokenExpiresOnUtc,
            int maxMembers, DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (members.Count >= maxMembers) return Task.FromResult(new CreateMemberStoreResult(CreateMemberOutcome.LimitReached));
            if (Find(email) is not null) return Task.FromResult(new CreateMemberStoreResult(CreateMemberOutcome.EmailExists));

            LastCreateVendorId = vendorId;
            LastTokenExpiry = setupTokenExpiresOnUtc;
            var id = nextId++;
            members.Add(new VendorMember { CustomerId = id, Email = email, FirstName = firstName, LastName = lastName, CreatedOnUtc = nowUtc });
            return Task.FromResult(new CreateMemberStoreResult(CreateMemberOutcome.Created, id));
        }

        public Task ReplaceSetupTokenAsync(int customerId, string tokenHash, DateTime expiresOnUtc, DateTime nowUtc, CancellationToken cancellationToken)
        {
            TokenReplacements++;
            return Task.CompletedTask;
        }

        public Task<RemoveMemberOutcome> RemoveAsync(int vendorId, int customerId, CancellationToken cancellationToken)
        {
            var member = members.FirstOrDefault(m => m.CustomerId == customerId);
            if (member is null) return Task.FromResult(RemoveMemberOutcome.NotMember);
            if (members.Count <= 1) return Task.FromResult(RemoveMemberOutcome.LastMember);
            members.Remove(member);
            return Task.FromResult(RemoveMemberOutcome.Removed);
        }
    }
}
