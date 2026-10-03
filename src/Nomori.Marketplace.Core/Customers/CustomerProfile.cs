namespace Nomori.Marketplace.Core.Customers;

public sealed class CustomerProfile
{
    public int CustomerId { get; set; }

    public string Email { get; set; } = string.Empty;

    public bool EmailVerified { get; set; }

    public string? Username { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Gender { get; set; }

    public DateTime? DateOfBirth { get; set; }

    public string? Phone { get; set; }

    /// <summary>Media asset shown as the avatar; 0 means none, and the client shows a default avatar.</summary>
    public int AvatarPictureId { get; set; }
}

public sealed record UpdateCustomerProfileCommand(
    int CustomerId,
    string? FirstName,
    string? LastName,
    string? Gender,
    DateTime? DateOfBirth,
    string? Phone,
    int? AvatarPictureId = null);

public sealed record CustomerProfileValidationResult(bool Succeeded, IReadOnlyDictionary<string, string[]> Errors)
{
    public static CustomerProfileValidationResult Success() => new(true, new Dictionary<string, string[]>());

    public static CustomerProfileValidationResult Failure(IReadOnlyDictionary<string, string[]> errors) => new(false, errors);
}
