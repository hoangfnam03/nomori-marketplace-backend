using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Authentication;

public sealed class EmailVerificationService(
    ICustomerIdentityStore customerStore,
    IEmailVerificationStore verificationStore,
    IEmailSender emailSender,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<SecurityOptions> securityOptions,
    IOptions<EmailOptions> emailOptions) : IEmailVerificationService
{
    public async Task<string?> SendVerificationAsync(int customerId, CancellationToken cancellationToken)
    {
        var customer = await customerStore.FindByIdAsync(customerId, cancellationToken);
        if (customer is null || customer.Deleted)
            return null;

        if (customer.EmailVerified)
            return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(token);
        await verificationStore.CreateAsync(customerId, tokenHash,
            clock.UtcNow.AddHours(securityOptions.Value.EmailVerificationTokenLifetimeHours), cancellationToken);

        if (emailOptions.Value.Enabled)
        {
            var link = BuildVerificationLink(token);
            var encodedLink = HtmlEncoder.Default.Encode(link);
            await emailSender.SendEmailAsync(new EmailMessage(
                customer.Email,
                emailOptions.Value.EmailVerificationSubject,
                $"<p>Welcome to Nomori Marketplace.</p><p><a href=\"{encodedLink}\">Verify your email address</a></p><p>This link expires in {securityOptions.Value.EmailVerificationTokenLifetimeHours} hours and can only be used once.</p>",
                $"Verify your Nomori Marketplace email: {link}"), cancellationToken);
        }

        await auditLog.WriteAsync("auth.email_verification_requested", customerId, details: new { deliveryEnabled = emailOptions.Value.Enabled }, cancellationToken: cancellationToken);
        return token;
    }

    public async Task<bool> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var tokenHash = HashToken(token);
        var record = await verificationStore.FindAsync(tokenHash, cancellationToken);
        if (record is null || record.Value.Used || record.Value.ExpiresOnUtc <= clock.UtcNow)
        {
            await auditLog.WriteAsync("auth.email_verification_failed", details: new { reason = "invalid_or_expired_token" }, cancellationToken: cancellationToken);
            return false;
        }

        var customerId = await verificationStore.ConsumeAsync(tokenHash, clock.UtcNow, cancellationToken);
        if (customerId is null)
            return false;

        await verificationStore.MarkVerifiedAsync(customerId.Value, clock.UtcNow, cancellationToken);
        await auditLog.WriteAsync("auth.email_verified", customerId.Value, cancellationToken: cancellationToken);
        return true;
    }

    private string BuildVerificationLink(string token)
    {
        var baseUrl = emailOptions.Value.FrontendBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Email:FrontendBaseUrl must be configured when email delivery is enabled.");

        return $"{baseUrl}/auth/verify-email?token={Uri.EscapeDataString(token)}";
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
