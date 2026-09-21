using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Globalization;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Authentication;

public sealed class EmailOtpService(
    ICustomerIdentityStore customerStore,
    IEmailOtpStore otpStore,
    IEmailSender emailSender,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<SecurityOptions> securityOptions,
    IOptions<EmailOptions> emailOptions) : IEmailOtpService
{
    public async Task<EmailOtpResult> CreateAsync(int customerId, string purpose, CancellationToken cancellationToken)
    {
        var customer = await customerStore.FindByIdAsync(customerId, cancellationToken);
        if (customer is null || !customer.EmailVerified)
            return new EmailOtpResult(Guid.Empty, clock.UtcNow, null, CustomerIdentityErrors.EmailNotVerified);

        var now = clock.UtcNow;
        var latest = await otpStore.GetLatestAsync(customerId, purpose, cancellationToken);
        if (latest?.LastSentOnUtc is not null && latest.LastSentOnUtc.Value.AddSeconds(securityOptions.Value.EmailOtpResendDelaySeconds) > now)
            return new EmailOtpResult(Guid.Empty, latest.ExpiresOnUtc, null, CustomerIdentityErrors.OtpRecentlySent);

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var challenge = new EmailOtpChallenge(
            Guid.NewGuid(), customerId, purpose, HashCode(code), now,
            now.AddMinutes(securityOptions.Value.EmailOtpLifetimeMinutes), now, 0, false);
        await otpStore.CreateAsync(challenge, cancellationToken);

        if (emailOptions.Value.Enabled)
        {
            var safeCode = HtmlEncoder.Default.Encode(code);
            await emailSender.SendEmailAsync(new EmailMessage(
                customer.Email,
                emailOptions.Value.EmailOtpSubject,
                $"<p>Your Nomori Marketplace verification code is <strong>{safeCode}</strong>.</p><p>This code expires in {securityOptions.Value.EmailOtpLifetimeMinutes} minutes and can be used once.</p>",
                $"Your Nomori Marketplace verification code is {code}. It expires in {securityOptions.Value.EmailOtpLifetimeMinutes} minutes."), cancellationToken);
        }

        await auditLog.WriteAsync("auth.otp_requested", customerId, details: new { purpose, deliveryEnabled = emailOptions.Value.Enabled }, cancellationToken: cancellationToken);
        return new EmailOtpResult(challenge.ChallengeId, challenge.ExpiresOnUtc, emailOptions.Value.Enabled ? null : code, null);
    }

    public async Task<EmailOtpVerificationResult> VerifyAsync(Guid challengeId, string code, string purpose, CancellationToken cancellationToken)
    {
        var result = await otpStore.ConsumeAsync(challengeId, purpose, HashCode(code), clock.UtcNow,
            securityOptions.Value.EmailOtpMaxAttempts, cancellationToken);
        if (!result.Succeeded || result.Challenge is null)
        {
            await auditLog.WriteAsync("auth.otp_verification_failed", result.Challenge?.CustomerId, details: new { purpose, reason = result.FailureCode }, cancellationToken: cancellationToken);
            return EmailOtpVerificationResult.Failure(result.FailureCode ?? CustomerIdentityErrors.OtpInvalid);
        }

        var customer = await customerStore.FindByIdAsync(result.Challenge.CustomerId, cancellationToken);
        if (customer is null)
            return EmailOtpVerificationResult.Failure(CustomerIdentityErrors.InvalidCredentials);

        await auditLog.WriteAsync("auth.otp_verified", customer.Id, details: new { purpose }, cancellationToken: cancellationToken);
        return EmailOtpVerificationResult.Success(customer);
    }

    public async Task SetEnabledAsync(int customerId, bool enabled, CancellationToken cancellationToken)
    {
        await otpStore.SetEmailOtpEnabledAsync(customerId, enabled, cancellationToken);
        await auditLog.WriteAsync(enabled ? "auth.otp_enabled" : "auth.otp_disabled", customerId, cancellationToken: cancellationToken);
    }

    private static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));
}
