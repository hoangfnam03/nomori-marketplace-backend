using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Authentication;

namespace Nomori.Marketplace.Services.Customers;

public sealed class CustomerAccountDataService(ICustomerAccountDataStore store, ICustomerIdentityStore identityStore, IPasswordHasher passwordHasher, IEmailSender emailSender, IAuditLogService auditLog, IClock clock, IOptions<SecurityOptions> security, IOptions<EmailOptions> email) : ICustomerAccountDataService
{
    public Task<IReadOnlyList<CustomerAddress>> GetAddressesAsync(int customerId, CancellationToken cancellationToken) => store.GetAddressesAsync(customerId, cancellationToken);
    public async Task<(CustomerAddress? Address, IReadOnlyDictionary<string, string[]> Errors)> SaveAddressAsync(CustomerAddress address, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        address.FirstName = Clean(address.FirstName); address.LastName = Clean(address.LastName); address.Address1 = Clean(address.Address1); address.City = Clean(address.City); address.CountryCode = Clean(address.CountryCode).ToUpperInvariant(); address.PhoneNumber = Clean(address.PhoneNumber);
        if (address.FirstName.Length is < 1 or > 100) errors["firstName"] = ["First name is required and must be at most 100 characters."];
        if (address.LastName.Length is < 1 or > 100) errors["lastName"] = ["Last name is required and must be at most 100 characters."];
        if (address.Address1.Length is < 1 or > 200) errors["address1"] = ["Address line 1 is required and must be at most 200 characters."];
        if (address.City.Length is < 1 or > 100) errors["city"] = ["City is required and must be at most 100 characters."];
        if (address.CountryCode.Length != 2 || !address.CountryCode.All(char.IsLetter)) errors["countryCode"] = ["Country must be a two-letter ISO code."];
        if (address.PhoneNumber.Length is < 1 or > 32 || address.PhoneNumber.Any(c => !char.IsDigit(c) && !"+ -()".Contains(c))) errors["phoneNumber"] = ["Phone number is required and contains unsupported characters."];
        if (errors.Count > 0) return (null, errors);
        var isNew = address.Id == 0;
        address.Id = await store.SaveAddressAsync(address, cancellationToken);
        await auditLog.WriteAsync(isNew ? "customer.address_created" : "customer.address_updated", address.CustomerId, entityType: "CustomerAddress", entityId: address.Id, cancellationToken: cancellationToken);
        return (address, errors);
    }
    public async Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) { var deleted = await store.DeleteAddressAsync(customerId, addressId, cancellationToken); if (deleted) await auditLog.WriteAsync("customer.address_deleted", customerId, entityType: "CustomerAddress", entityId: addressId, cancellationToken: cancellationToken); return deleted; }
    public Task<CustomerAttributeSet> GetAttributesAsync(int customerId, CancellationToken cancellationToken) => store.GetAttributesAsync(customerId, cancellationToken);
    public async Task<IReadOnlyDictionary<string, string[]>> SaveAttributesAsync(int customerId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
    {
        var current = await store.GetAttributesAsync(customerId, cancellationToken); var errors = new Dictionary<string, string[]>(); var saved = new Dictionary<string, string>();
        foreach (var definition in current.Definitions) { values.TryGetValue(definition.SystemName, out var value); value = value?.Trim() ?? string.Empty; if (definition.IsRequired && string.IsNullOrWhiteSpace(value)) errors[definition.SystemName] = [$"{definition.Name} is required."]; else if (definition.DataType == "boolean" && value is not "" and not "true" and not "false") errors[definition.SystemName] = [$"{definition.Name} must be true or false."]; else if (value.Length > 500) errors[definition.SystemName] = [$"{definition.Name} cannot exceed 500 characters."]; else saved[definition.SystemName] = value; }
        if (errors.Count > 0) return errors; await store.SaveAttributesAsync(customerId, saved, cancellationToken); await auditLog.WriteAsync("customer.attributes_updated", customerId, details: new { attributes = saved.Keys }, cancellationToken: cancellationToken); return errors;
    }
    public async Task<EmailChangeRequestResult> RequestEmailChangeAsync(int customerId, string newEmail, string currentPassword, CancellationToken cancellationToken)
    {
        newEmail = newEmail.Trim().ToLowerInvariant(); var customer = await identityStore.FindByIdAsync(customerId, cancellationToken); var password = await identityStore.GetLatestPasswordAsync(customerId, cancellationToken);
        if (customer is null || password is null || !passwordHasher.Verify(currentPassword, password)) return new(false, "auth.invalid_credentials");
        if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(newEmail)) return new(false, "customer.email_invalid");
        if (string.Equals(customer.Email, newEmail, StringComparison.OrdinalIgnoreCase) || await store.EmailExistsAsync(newEmail, cancellationToken)) return new(false, "customer.email_unavailable");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); await store.CreateEmailChangeAsync(customerId, newEmail, Hash(token), clock.UtcNow.AddHours(security.Value.EmailVerificationTokenLifetimeHours), cancellationToken);
        if (email.Value.Enabled) { var link = $"{email.Value.FrontendBaseUrl.TrimEnd('/')}/auth/confirm-email-change?token={Uri.EscapeDataString(token)}"; await emailSender.SendEmailAsync(new EmailMessage(newEmail, "Confirm your Nomori Marketplace email change", $"<p><a href=\"{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(link)}\">Confirm email change</a></p>", $"Confirm your Nomori Marketplace email change: {link}"), cancellationToken); }
        await auditLog.WriteAsync("customer.email_change_requested", customerId, details: new { deliveryEnabled = email.Value.Enabled }, cancellationToken: cancellationToken); return new(true, null, email.Value.Enabled ? null : token);
    }
    public async Task<bool> ConfirmEmailChangeAsync(string token, CancellationToken cancellationToken) { if (string.IsNullOrWhiteSpace(token)) return false; var request = await store.ConsumeEmailChangeAsync(Hash(token), clock.UtcNow, cancellationToken); if (request is null || await store.EmailExistsAsync(request.Value.NewEmail, cancellationToken)) return false; await store.ApplyEmailChangeAsync(request.Value.CustomerId, request.Value.NewEmail, clock.UtcNow, cancellationToken); await auditLog.WriteAsync("customer.email_changed", request.Value.CustomerId, cancellationToken: cancellationToken); return true; }
    private static string Clean(string? value) => value?.Trim() ?? string.Empty; private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
