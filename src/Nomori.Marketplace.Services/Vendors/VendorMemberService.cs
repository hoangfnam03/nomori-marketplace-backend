using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Authentication;

namespace Nomori.Marketplace.Services.Vendors;

public sealed partial class VendorMemberService(
    IVendorStore vendorStore,
    IVendorMemberStore memberStore,
    IPasswordHasher passwordHasher,
    IEmailSender emailSender,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<VendorOptions> vendorOptions,
    IOptions<SecurityOptions> securityOptions,
    IOptions<EmailOptions> emailOptions,
    ILogger<VendorMemberService> logger) : IVendorMemberService
{
    private const int MaxNameLength = 100;

    public async Task<VendorResult<IReadOnlyList<VendorMember>>> ListAsync(
        int vendorId, VendorCaller caller, CancellationToken cancellationToken)
    {
        if (await GetAccessibleVendorAsync(vendorId, caller, cancellationToken) is null)
            return VendorResult.Error<IReadOnlyList<VendorMember>>(VendorErrors.NotFound);

        return VendorResult.Success(await memberStore.ListAsync(vendorId, cancellationToken));
    }

    public async Task<VendorResult<VendorMemberSetup>> CreateAsync(
        int vendorId, CreateVendorMemberCommand command, VendorCaller caller, CancellationToken cancellationToken)
    {
        var vendor = await GetAccessibleVendorAsync(vendorId, caller, cancellationToken);
        if (vendor is null) return VendorResult.Error<VendorMemberSetup>(VendorErrors.NotFound);
        // Shop accounts are created by the shop itself; administrators cannot create them, and a shop that is switched off is read-only.
        if (!caller.IsMemberOf(vendorId) || !vendor.Active)
            return VendorResult.Error<VendorMemberSetup>(VendorErrors.Forbidden);

        var errors = new Dictionary<string, string[]>();
        var email = VendorValidation.NormalizeEmail(command.Email);
        VendorValidation.ValidateEmail(email, errors);
        VendorValidation.ValidateMaxLength(command.FirstName, MaxNameLength, "firstName", "First name", errors);
        VendorValidation.ValidateMaxLength(command.LastName, MaxNameLength, "lastName", "Last name", errors);
        if (errors.Count > 0) return VendorResult.Failure<VendorMemberSetup>(errors);

        // The password is random, hashed and never shown: the new member cannot sign in until the emailed link is used.
        var unusablePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (passwordHash, passwordSalt) = passwordHasher.HashPassword(unusablePassword);
        var token = NewToken();
        var now = clock.UtcNow;

        var result = await memberStore.CreateAsync(
            vendorId, email,
            VendorValidation.NullIfBlank(command.FirstName), VendorValidation.NullIfBlank(command.LastName),
            passwordHash, passwordSalt,
            HashToken(token), now.AddHours(securityOptions.Value.VendorMemberSetupTokenLifetimeHours),
            vendorOptions.Value.MaxMembersPerVendor, now, cancellationToken);

        switch (result.Outcome)
        {
            case CreateMemberOutcome.EmailExists:
                return VendorResult.Error<VendorMemberSetup>(VendorErrors.MemberEmailAlreadyExists);
            case CreateMemberOutcome.LimitReached:
                return VendorResult.Error<VendorMemberSetup>(VendorErrors.MemberLimitReached);
        }

        await auditLog.WriteAsync("vendor.member_created", caller.CustomerId, result.CustomerId, "Vendor", vendorId,
            details: new { vendorId, customerId = result.CustomerId, actor = caller.CustomerId },
            cancellationToken: cancellationToken);

        await SendSetupEmailAsync(email, vendor.Name, token, cancellationToken);

        var member = await memberStore.GetAsync(vendorId, result.CustomerId, cancellationToken)
            ?? new VendorMember { CustomerId = result.CustomerId, Email = email, CreatedOnUtc = now };
        return VendorResult.Success(new VendorMemberSetup(member, token));
    }

    public async Task<VendorResult<string>> ResendSetupEmailAsync(
        int vendorId, int customerId, VendorCaller caller, CancellationToken cancellationToken)
    {
        var vendor = await GetAccessibleVendorAsync(vendorId, caller, cancellationToken);
        if (vendor is null) return VendorResult.Error<string>(VendorErrors.NotFound);
        if (!caller.IsMemberOf(vendorId)) return VendorResult.Error<string>(VendorErrors.Forbidden);

        var member = await memberStore.GetAsync(vendorId, customerId, cancellationToken);
        if (member is null) return VendorResult.Error<string>(VendorErrors.NotFound);
        if (member.Status == VendorMemberStatus.Active) return VendorResult.Error<string>(VendorErrors.MemberAlreadyActive);

        var token = NewToken();
        var now = clock.UtcNow;
        await memberStore.ReplaceSetupTokenAsync(
            customerId, HashToken(token), now.AddHours(securityOptions.Value.VendorMemberSetupTokenLifetimeHours), now, cancellationToken);

        await auditLog.WriteAsync("vendor.member_setup_resent", caller.CustomerId, customerId, "Vendor", vendorId,
            details: new { vendorId, customerId, actor = caller.CustomerId }, cancellationToken: cancellationToken);

        await SendSetupEmailAsync(member.Email, vendor.Name, token, cancellationToken);
        return VendorResult.Success(token);
    }

    public async Task<VendorResult<bool>> RemoveAsync(
        int vendorId, int customerId, VendorCaller caller, CancellationToken cancellationToken)
    {
        if (await GetAccessibleVendorAsync(vendorId, caller, cancellationToken) is null)
            return VendorResult.Error<bool>(VendorErrors.NotFound);

        switch (await memberStore.RemoveAsync(vendorId, customerId, cancellationToken))
        {
            case RemoveMemberOutcome.NotMember:
                return VendorResult.Error<bool>(VendorErrors.NotFound);
            case RemoveMemberOutcome.LastMember:
                return VendorResult.Error<bool>(VendorErrors.MemberLastMember);
        }

        await auditLog.WriteAsync("vendor.member_removed", caller.CustomerId, customerId, "Vendor", vendorId,
            details: new
            {
                vendorId,
                customerId,
                actor = caller.CustomerId,
                self = caller.CustomerId == customerId,
                byAdmin = caller.IsAdmin && !caller.IsMemberOf(vendorId)
            },
            cancellationToken: cancellationToken);
        return VendorResult.Success(true);
    }

    /// <summary>The vendor, but only for its own members and administrators. Everyone else sees "not found".</summary>
    private async Task<Vendor?> GetAccessibleVendorAsync(int vendorId, VendorCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.IsAuthenticated || !(caller.IsAdmin || caller.IsMemberOf(vendorId))) return null;
        return await vendorStore.GetAsync(vendorId, cancellationToken);
    }

    /// <summary>Sends the set-password email. A delivery failure is logged; the member can ask for the email to be resent.</summary>
    private async Task SendSetupEmailAsync(string toAddress, string shopName, string token, CancellationToken cancellationToken)
    {
        if (!emailOptions.Value.Enabled) return;

        try
        {
            var baseUrl = emailOptions.Value.FrontendBaseUrl.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Email:FrontendBaseUrl must be configured when email delivery is enabled.");

            var link = $"{baseUrl}/auth/reset-password?token={Uri.EscapeDataString(token)}&setup=1";
            var encoder = HtmlEncoder.Default;
            var hours = securityOptions.Value.VendorMemberSetupTokenLifetimeHours;
            await emailSender.SendEmailAsync(new EmailMessage(
                toAddress,
                emailOptions.Value.VendorMemberSetupSubject,
                $"<p>You were added to the shop <strong>{encoder.Encode(shopName)}</strong> on Nomori Marketplace.</p>" +
                $"<p><a href=\"{encoder.Encode(link)}\">Set your password</a></p>" +
                $"<p>This link expires in {hours} hours and can only be used once.</p>",
                $"Set your Nomori Marketplace password for the shop {shopName}: {link}"), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEmailFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send vendor member setup email.")]
    private partial void LogEmailFailed(Exception exception);

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
