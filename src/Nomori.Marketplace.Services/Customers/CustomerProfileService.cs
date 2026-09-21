using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Customers;

public sealed class CustomerProfileService(
    ICustomerProfileStore profileStore,
    IAuditLogService auditLog,
    IClock clock) : ICustomerProfileService
{
    private static readonly HashSet<string> AllowedGenders = new(StringComparer.OrdinalIgnoreCase)
    {
        "male", "female", "other", "unspecified"
    };

    public Task<CustomerProfile?> GetAsync(int customerId, CancellationToken cancellationToken) =>
        profileStore.GetAsync(customerId, cancellationToken);

    public async Task<CustomerProfileUpdateResult> UpdateAsync(UpdateCustomerProfileCommand command, CancellationToken cancellationToken)
    {
        var current = await profileStore.GetAsync(command.CustomerId, cancellationToken);
        if (current is null)
            return CustomerProfileUpdateResult.Failure(new Dictionary<string, string[]>
            {
                ["profile"] = ["Customer profile was not found."]
            });

        var firstName = Normalize(command.FirstName);
        var lastName = Normalize(command.LastName);
        var gender = Normalize(command.Gender)?.ToLowerInvariant();
        var phone = Normalize(command.Phone);
        var errors = Validate(firstName, lastName, gender, command.DateOfBirth, phone);
        if (errors.Count > 0)
            return CustomerProfileUpdateResult.Failure(errors);

        var updated = new CustomerProfile
        {
            CustomerId = current.CustomerId,
            Email = current.Email,
            EmailVerified = current.EmailVerified,
            Username = current.Username,
            FirstName = firstName,
            LastName = lastName,
            Gender = gender,
            DateOfBirth = command.DateOfBirth?.Date,
            Phone = phone
        };
        await profileStore.UpdateAsync(updated, cancellationToken);

        var changedFields = new List<string>();
        if (!string.Equals(current.FirstName, updated.FirstName, StringComparison.Ordinal)) changedFields.Add("firstName");
        if (!string.Equals(current.LastName, updated.LastName, StringComparison.Ordinal)) changedFields.Add("lastName");
        if (!string.Equals(current.Gender, updated.Gender, StringComparison.Ordinal)) changedFields.Add("gender");
        if (current.DateOfBirth?.Date != updated.DateOfBirth?.Date) changedFields.Add("dateOfBirth");
        if (!string.Equals(current.Phone, updated.Phone, StringComparison.Ordinal)) changedFields.Add("phone");

        await auditLog.WriteAsync(
            "customer.profile_updated",
            command.CustomerId,
            entityType: "CustomerProfile",
            entityId: command.CustomerId,
            details: new { changedFields },
            cancellationToken: cancellationToken);

        return new CustomerProfileUpdateResult(updated, new Dictionary<string, string[]>());
    }

    private Dictionary<string, string[]> Validate(string? firstName, string? lastName, string? gender, DateTime? dateOfBirth, string? phone)
    {
        var errors = new Dictionary<string, string[]>();
        if (firstName?.Length > 100) errors["firstName"] = ["First name cannot exceed 100 characters."];
        if (lastName?.Length > 100) errors["lastName"] = ["Last name cannot exceed 100 characters."];
        if (gender is not null && !AllowedGenders.Contains(gender)) errors["gender"] = ["Gender must be male, female, other or unspecified."];
        if (dateOfBirth is not null && (dateOfBirth.Value.Date < new DateTime(1900, 1, 1) || dateOfBirth.Value.Date > clock.UtcNow.Date))
            errors["dateOfBirth"] = ["Date of birth must be between 1900-01-01 and today."];
        if (phone?.Length > 32 || phone is not null && phone.Any(character => !char.IsDigit(character) && !"+ -()".Contains(character)))
            errors["phone"] = ["Phone number contains unsupported characters or is too long."];
        return errors;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
