using Nomori.Marketplace.Core.Domain;

namespace Nomori.Marketplace.Core.Domain.Customers;

public sealed class Customer : BaseEntity
{
    public Guid CustomerGuid { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Gender { get; set; }

    public DateTime? DateOfBirth { get; set; }

    public string? Phone { get; set; }

    public bool EmailVerified { get; set; }

    public DateTime? EmailVerifiedOnUtc { get; set; }

    public bool EmailOtpEnabled { get; set; }

    public bool Active { get; set; } = true;

    public bool Deleted { get; set; }

    public int FailedLoginAttempts { get; set; }

    public DateTime? CannotLoginUntilDateUtc { get; set; }

    public bool RequireReLogin { get; set; }

    public DateTime CreatedOnUtc { get; set; }

    public DateTime? LastLoginDateUtc { get; set; }
}
