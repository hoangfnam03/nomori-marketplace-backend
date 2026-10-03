using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Nomori.Marketplace.Services.Vendors;

internal static partial class VendorValidation
{
    public const int MaxNameLength = 400;
    public const int MaxEmailLength = 320;
    public const int MaxTaxCodeLength = 50;
    public const int MaxBusinessAddressLength = 1000;

    [GeneratedRegex(@"^[0-9\s+\-()]+$")]
    private static partial Regex PhonePattern();

    public static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed)
        && string.Equals(parsed.Address, email, StringComparison.Ordinal)
        && parsed.Host.Contains('.');

    public static void ValidateShopName(string? name, IDictionary<string, string[]> errors, string field = "shopName")
    {
        if (string.IsNullOrWhiteSpace(name)) errors[field] = ["Shop name is required."];
        else if (name.Trim().Length > MaxNameLength) errors[field] = [$"Shop name cannot exceed {MaxNameLength} characters."];
    }

    public static void ValidateEmail(string normalizedEmail, IDictionary<string, string[]> errors, string field = "email")
    {
        if (normalizedEmail.Length == 0) errors[field] = ["Email is required."];
        else if (normalizedEmail.Length > MaxEmailLength) errors[field] = [$"Email cannot exceed {MaxEmailLength} characters."];
        else if (!IsValidEmail(normalizedEmail)) errors[field] = ["Email is not a valid email address."];
    }

    public static void ValidatePhone(string? phone, IDictionary<string, string[]> errors)
    {
        var value = phone?.Trim() ?? string.Empty;
        if (value.Length == 0) errors["phoneNumber"] = ["Phone number is required."];
        else if (value.Length > 50 || !PhonePattern().IsMatch(value))
            errors["phoneNumber"] = ["Phone number contains unsupported characters or is too long."];
    }

    public static void ValidateMaxLength(string? value, int max, string field, string label, IDictionary<string, string[]> errors)
    {
        if (value is not null && value.Trim().Length > max) errors[field] = [$"{label} cannot exceed {max} characters."];
    }
}
