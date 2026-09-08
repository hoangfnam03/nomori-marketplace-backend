using Nomori.Marketplace.Core.Domain;

namespace Nomori.Marketplace.Core.Domain.Customers;

public sealed class CustomerPassword : BaseEntity
{
    public int CustomerId { get; set; }

    public string Password { get; set; } = string.Empty;

    public PasswordFormat PasswordFormat { get; set; } = PasswordFormat.Hashed;

    public string? PasswordSalt { get; set; }

    public DateTime CreatedOnUtc { get; set; }
}