using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Tests;

internal sealed class TestClock : IClock
{
    public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}

internal sealed class RecordingAuditLog : IAuditLogService
{
    public List<(string Event, int? CustomerId, int? TargetCustomerId, object? Details)> Entries { get; } = [];

    public Task WriteAsync(string eventName, int? customerId = null, int? targetCustomerId = null, string? entityType = null,
        int? entityId = null, string? ipAddress = null, object? details = null, CancellationToken cancellationToken = default)
    {
        Entries.Add((eventName, customerId, targetCustomerId, details));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditLog>> GetRecentAsync(int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditLog>>([]);

    public IEnumerable<string> Events => Entries.Select(e => e.Event);
}

internal sealed class RecordingEmailSender : IEmailSender
{
    public bool Throw { get; set; }
    public List<EmailMessage> Sent { get; } = [];

    public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Throw) throw new InvalidOperationException("SMTP down");
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

internal static class TestOptions
{
    public static IOptions<EmailOptions> Email(bool enabled) =>
        Options.Create(new EmailOptions { Enabled = enabled, FrontendBaseUrl = "https://shop.test" });
}

internal sealed class FakeCustomerIdentityStore(params Customer[] customers) : ICustomerIdentityStore
{
    public List<Customer> Customers { get; } = [.. customers];

    public Task<Customer?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        Task.FromResult(Customers.FirstOrDefault(c => c.Email == email));

    public Task<Customer?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(Customers.FirstOrDefault(c => c.Id == id));

    public Task<CustomerPassword?> GetLatestPasswordAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<CustomerPassword>> GetPasswordHistoryAsync(int customerId, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> CreateCustomerAsync(Customer customer, CustomerPassword password, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task UpdateCustomerAsync(Customer customer, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AddPasswordAsync(CustomerPassword password, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task ChangePasswordAsync(Customer customer, CustomerPassword password, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task CreateRecoveryTokenAsync(int customerId, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<(int CustomerId, string TokenHash, DateTime ExpiresOnUtc, bool Used)?> FindRecoveryTokenAsync(string tokenHash, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task MarkRecoveryTokenUsedAsync(string tokenHash, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> ResetPasswordWithRecoveryTokenAsync(string tokenHash, DateTime nowUtc, CustomerPassword password, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlySet<string>> GetPermissionCodesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeVendorStore : IVendorStore
{
    public List<Vendor> Vendors { get; } = [];
    public IReadOnlyList<int>? FormerMembers { get; set; } = [];
    public bool DeleteCalled { get; private set; }

    public Task<Vendor?> GetAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(Vendors.FirstOrDefault(v => v.Id == id && !v.Deleted));

    public Task<Vendor?> GetByCustomerIdAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<(IReadOnlyList<Vendor> Items, int TotalCount)> GetPagedAsync(VendorQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task UpdateAsync(Vendor vendor, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<int>?> DeleteAsync(int id, DateTime nowUtc, CancellationToken cancellationToken)
    {
        DeleteCalled = true;
        return Task.FromResult(FormerMembers);
    }

    public Task<(IReadOnlyList<VendorNote> Items, int TotalCount)> GetNotesPagedAsync(int vendorId, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> InsertNoteAsync(VendorNote note, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> DeleteNoteAsync(int vendorId, int noteId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal static class NullLog<T>
{
    public static NullLogger<T> Instance { get; } = NullLogger<T>.Instance;
}
