using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Vendors;

namespace Nomori.Marketplace.Services.Tests;

public sealed class VendorApplicationServiceTests
{
    private static readonly VendorCaller Applicant = new(10, false, null);
    private static readonly VendorCaller Admin = new(1, true, null);

    private static SubmitVendorApplicationCommand ValidSubmit(string shopName = "Mai Ceramics") =>
        new(shopName, "Shop@Example.com ", "+84 901 234 567", " Handmade ", null, "");

    private sealed class Fixture
    {
        public FakeApplicationStore Store { get; } = new();
        public FakeCustomerIdentityStore Customers { get; } =
            new(new Customer { Id = 10, Email = "a@example.com", EmailVerified = true },
                new Customer { Id = 11, Email = "unverified@example.com", EmailVerified = false });
        public RecordingEmailSender Email { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public bool EmailEnabled { get; set; }

        public VendorApplicationService Create() => new(
            Store, Customers, Email, Audit, new TestClock(), TestOptions.Email(EmailEnabled), NullLog<VendorApplicationService>.Instance);
    }

    [Fact]
    public async Task SubmitNormalizesValuesAndAudits()
    {
        var f = new Fixture();
        var result = await f.Create().SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("shop@example.com", f.Store.Applications.Single().Email);
        Assert.Equal("Handmade", f.Store.Applications.Single().Description);
        Assert.Null(f.Store.Applications.Single().BusinessAddress);
        Assert.Contains("vendor.application_submitted", f.Audit.Events);
    }

    [Fact]
    public async Task SubmitRejectsUnverifiedEmail()
    {
        var f = new Fixture();
        var result = await f.Create().SubmitAsync(ValidSubmit(), new VendorCaller(11, false, null), CancellationToken.None);

        Assert.Equal(VendorErrors.ApplicationEmailNotVerified, result.ErrorCode);
        Assert.Empty(f.Store.Applications);
    }

    [Fact]
    public async Task SubmitRejectsAccountThatAlreadyBelongsToAShop()
    {
        var f = new Fixture();
        var result = await f.Create().SubmitAsync(ValidSubmit(), new VendorCaller(10, false, 5), CancellationToken.None);

        Assert.Equal(VendorErrors.ApplicationAlreadyVendor, result.ErrorCode);
    }

    [Fact]
    public async Task SubmitRejectsSecondPendingApplication()
    {
        var f = new Fixture();
        var service = f.Create();
        await service.SubmitAsync(ValidSubmit("First"), Applicant, CancellationToken.None);

        var second = await service.SubmitAsync(ValidSubmit("Second"), Applicant, CancellationToken.None);

        Assert.Equal(VendorErrors.ApplicationAlreadyPending, second.ErrorCode);
    }

    [Fact]
    public async Task SubmitRejectsDuplicateShopNameAndInvalidFields()
    {
        var f = new Fixture();
        f.Store.TakenShopNames.Add("Mai Ceramics");

        var duplicate = await f.Create().SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None);
        Assert.Contains("shopName", duplicate.Errors.Keys);

        var invalid = await f.Create().SubmitAsync(new SubmitVendorApplicationCommand("", "not-an-email", "abc", null, null, null), Applicant, CancellationToken.None);
        Assert.Contains("shopName", invalid.Errors.Keys);
        Assert.Contains("email", invalid.Errors.Keys);
        Assert.Contains("phoneNumber", invalid.Errors.Keys);
    }

    [Fact]
    public async Task UpdateAndCancelOnlyWorkWhilePending()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None)).Value!.Id;

        var update = await service.UpdateAsync(id, new UpdateVendorApplicationCommand("New Name", "shop@example.com", "+84 901 234 567", null, null, null), Applicant, CancellationToken.None);
        Assert.True(update.Succeeded);
        Assert.Contains("vendor.application_updated", f.Audit.Events);

        var cancel = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Cancelled, null, null, null), Applicant, CancellationToken.None);
        Assert.True(cancel.Succeeded);
        Assert.Contains("vendor.application_cancelled", f.Audit.Events);

        var updateAgain = await service.UpdateAsync(id, new UpdateVendorApplicationCommand("Other", "shop@example.com", "+84 901 234 567", null, null, null), Applicant, CancellationToken.None);
        Assert.Equal(VendorErrors.ApplicationNotPending, updateAgain.ErrorCode);

        var approveAgain = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Approved, null, null, null), Admin, CancellationToken.None);
        Assert.Equal(VendorErrors.ApplicationNotPending, approveAgain.ErrorCode);
    }

    [Fact]
    public async Task RejectRequiresReasonAndEmailsApplicantOnlyWhenEnabled()
    {
        var f = new Fixture { EmailEnabled = true };
        var service = f.Create();
        var id = (await service.SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None)).Value!.Id;

        var missing = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Rejected, " ", null, null), Admin, CancellationToken.None);
        Assert.Contains("reason", missing.Errors.Keys);

        var rejected = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Rejected, "Tax code <b>mismatch</b>", null, null), Admin, CancellationToken.None);
        Assert.True(rejected.Succeeded);
        Assert.Equal(VendorApplicationStatus.Rejected, rejected.Value!.Status);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("a@example.com", email.ToAddress);
        Assert.DoesNotContain("<b>", email.HtmlBody);
        Assert.Contains("vendor.application_rejected", f.Audit.Events);

        var disabled = new Fixture();
        var disabledService = disabled.Create();
        var otherId = (await disabledService.SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None)).Value!.Id;
        await disabledService.ChangeStatusAsync(otherId, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Rejected, "No", null, null), Admin, CancellationToken.None);
        Assert.Empty(disabled.Email.Sent);
    }

    [Fact]
    public async Task ApproveCreatesVendorAuditsAndToleratesEmailFailure()
    {
        var f = new Fixture { EmailEnabled = true };
        f.Email.Throw = true;
        var service = f.Create();
        var id = (await service.SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None)).Value!.Id;

        var approved = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Approved, null, "Mai Ceramics Official", "Verified"), Admin, CancellationToken.None);

        Assert.True(approved.Succeeded);
        Assert.Equal(VendorApplicationStatus.Approved, approved.Value!.Status);
        Assert.Equal("Mai Ceramics Official", f.Store.ApprovedShopName);
        Assert.Equal("Verified", f.Store.ApprovedAdminComment);
        Assert.Contains("vendor.application_approved", f.Audit.Events);
    }

    [Fact]
    public async Task ApproveFailsWhenApplicantJoinedAShopMeanwhile()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None)).Value!.Id;
        f.Store.ApplicantAlreadyVendor = true;

        var result = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Approved, null, null, null), Admin, CancellationToken.None);

        Assert.Equal(VendorErrors.ApplicantAlreadyVendor, result.ErrorCode);
        Assert.DoesNotContain("vendor.application_approved", f.Audit.Events);
    }

    [Fact]
    public async Task CustomersCannotReviewAndAdminsCannotCancelOthersApplications()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None)).Value!.Id;

        var approveByApplicant = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Approved, null, null, null), Applicant, CancellationToken.None);
        Assert.Equal(VendorErrors.Forbidden, approveByApplicant.ErrorCode);

        var cancelByAdmin = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Cancelled, null, null, null), Admin, CancellationToken.None);
        Assert.Equal(VendorErrors.Forbidden, cancelByAdmin.ErrorCode);

        var stranger = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Cancelled, null, null, null), new VendorCaller(99, false, null), CancellationToken.None);
        Assert.Equal(VendorErrors.NotFound, stranger.ErrorCode);

        var adminEdit = await service.UpdateAsync(id, new UpdateVendorApplicationCommand("x", "a@example.com", "123", null, null, null), Admin, CancellationToken.None);
        Assert.Equal(VendorErrors.Forbidden, adminEdit.ErrorCode);

        var pendingTarget = await service.ChangeStatusAsync(id, new ChangeVendorApplicationStatusCommand(VendorApplicationStatus.Pending, null, null, null), Admin, CancellationToken.None);
        Assert.Contains("status", pendingTarget.Errors.Keys);
    }

    [Fact]
    public async Task ListScopesCustomersToTheirOwnApplicationsAndIgnoresSearch()
    {
        var f = new Fixture();
        var service = f.Create();
        await service.SubmitAsync(ValidSubmit(), Applicant, CancellationToken.None);

        await service.GetListAsync(new VendorApplicationQuery(Search: "anything"), Applicant, CancellationToken.None);
        Assert.Equal(10, f.Store.LastQuery!.CustomerId);
        Assert.Null(f.Store.LastQuery.Search);

        await service.GetListAsync(new VendorApplicationQuery(Status: VendorApplicationStatus.Pending, Search: "mai"), Admin, CancellationToken.None);
        Assert.Null(f.Store.LastQuery.CustomerId);
        Assert.Equal("mai", f.Store.LastQuery.Search);
        Assert.True(f.Store.LastQuery.OldestFirst);
    }

    private sealed class FakeApplicationStore : IVendorApplicationStore
    {
        private int nextId = 1;

        public List<VendorApplication> Applications { get; } = [];
        public HashSet<string> TakenShopNames { get; } = [];
        public bool ApplicantAlreadyVendor { get; set; }
        public string? ApprovedShopName { get; private set; }
        public string? ApprovedAdminComment { get; private set; }
        public VendorApplicationQuery? LastQuery { get; private set; }

        public Task<VendorApplication?> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(Applications.FirstOrDefault(a => a.Id == id));

        public Task<(IReadOnlyList<VendorApplication> Items, int TotalCount)> GetPagedAsync(VendorApplicationQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult<(IReadOnlyList<VendorApplication>, int)>(([], 0));
        }

        public Task<bool> ShopNameExistsAsync(string shopName, int? excludeApplicationId, CancellationToken cancellationToken) =>
            Task.FromResult(TakenShopNames.Contains(shopName)
                || Applications.Any(a => a.Id != excludeApplicationId && a.Status == VendorApplicationStatus.Pending
                                         && string.Equals(a.ShopName, shopName, StringComparison.OrdinalIgnoreCase)));

        public Task<int?> InsertAsync(VendorApplication application, CancellationToken cancellationToken)
        {
            if (Applications.Any(a => a.CustomerId == application.CustomerId && a.Status == VendorApplicationStatus.Pending))
                return Task.FromResult<int?>(null);
            application.Id = nextId++;
            application.CustomerEmail = "a@example.com";
            Applications.Add(application);
            return Task.FromResult<int?>(application.Id);
        }

        public Task<bool> UpdateContentAsync(VendorApplication application, CancellationToken cancellationToken) =>
            Task.FromResult(application.Status == VendorApplicationStatus.Pending);

        public Task<bool> CloseAsync(int id, VendorApplicationStatus status, string? rejectReason, int? reviewedByCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var application = Applications.Single(a => a.Id == id);
            if (application.Status != VendorApplicationStatus.Pending) return Task.FromResult(false);
            application.Status = status;
            application.RejectReason = rejectReason;
            application.ReviewedByCustomerId = reviewedByCustomerId;
            return Task.FromResult(true);
        }

        public Task<ApproveApplicationResult> ApproveAsync(int id, string shopName, string? adminComment, int reviewedByCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var application = Applications.Single(a => a.Id == id);
            if (application.Status != VendorApplicationStatus.Pending)
                return Task.FromResult(new ApproveApplicationResult(ApproveApplicationOutcome.NotPending));
            if (ApplicantAlreadyVendor)
                return Task.FromResult(new ApproveApplicationResult(ApproveApplicationOutcome.ApplicantAlreadyVendor));

            application.Status = VendorApplicationStatus.Approved;
            application.VendorId = 77;
            ApprovedShopName = shopName;
            ApprovedAdminComment = adminComment;
            return Task.FromResult(new ApproveApplicationResult(ApproveApplicationOutcome.Approved, 77));
        }
    }
}
