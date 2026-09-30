namespace Nomori.Marketplace.Core.Vendors;

public enum VendorMemberStatus
{
    PendingSetup = 0,
    Active = 1
}

public sealed class VendorMember
{
    public int CustomerId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime? LastLoginDateUtc { get; set; }

    public VendorMemberStatus Status => LastLoginDateUtc is null ? VendorMemberStatus.PendingSetup : VendorMemberStatus.Active;
}

public sealed record CreateVendorMemberCommand(string Email, string? FirstName, string? LastName);

public sealed record VendorMemberSetup(VendorMember Member, string SetupToken);

public enum CreateMemberOutcome
{
    Created,
    EmailExists,
    LimitReached
}

public sealed record CreateMemberStoreResult(CreateMemberOutcome Outcome, int CustomerId = 0);

public enum RemoveMemberOutcome
{
    Removed,
    NotMember,
    LastMember
}

public interface IVendorMemberStore
{
    Task<IReadOnlyList<VendorMember>> ListAsync(int vendorId, CancellationToken cancellationToken);

    Task<VendorMember?> GetAsync(int vendorId, int customerId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the customer, its unusable password, the Registered and Vendors roles, the vendor link and the setup token
    /// in one transaction. Refuses when the email exists or the shop already has <paramref name="maxMembers"/> members.
    /// </summary>
    Task<CreateMemberStoreResult> CreateAsync(
        int vendorId, string email, string? firstName, string? lastName,
        string passwordHash, string passwordSalt,
        string setupTokenHash, DateTime setupTokenExpiresOnUtc,
        int maxMembers, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Marks earlier unused tokens as used and stores the new one, in one transaction.</summary>
    Task ReplaceSetupTokenAsync(int customerId, string tokenHash, DateTime expiresOnUtc, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Clears the link, removes the Vendors role and sets RequireReLogin in one transaction. Refuses to remove the last member.</summary>
    Task<RemoveMemberOutcome> RemoveAsync(int vendorId, int customerId, CancellationToken cancellationToken);
}

public interface IVendorMemberService
{
    Task<VendorResult<IReadOnlyList<VendorMember>>> ListAsync(int vendorId, VendorCaller caller, CancellationToken cancellationToken);

    Task<VendorResult<VendorMemberSetup>> CreateAsync(int vendorId, CreateVendorMemberCommand command, VendorCaller caller, CancellationToken cancellationToken);

    /// <summary>Returns the new setup token.</summary>
    Task<VendorResult<string>> ResendSetupEmailAsync(int vendorId, int customerId, VendorCaller caller, CancellationToken cancellationToken);

    Task<VendorResult<bool>> RemoveAsync(int vendorId, int customerId, VendorCaller caller, CancellationToken cancellationToken);
}
