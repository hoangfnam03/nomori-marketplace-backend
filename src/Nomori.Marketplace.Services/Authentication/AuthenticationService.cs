using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Time;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Email;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace Nomori.Marketplace.Services.Authentication;

public sealed record RegisterCustomerCommand(string Email, string Password);

public sealed record LoginCustomerCommand(string Email, string Password);

public sealed record ChangePasswordCommand(int CustomerId, string CurrentPassword, string NewPassword);

public sealed record ResetPasswordCommand(string Token, string NewPassword);

public sealed record AuthenticationResult(Customer? Customer, string? ErrorCode)
{
    public bool Succeeded => Customer is not null && ErrorCode is null;
}

public interface IAuthenticationService
{
    Task<AuthenticationResult> RegisterAsync(RegisterCustomerCommand command, CancellationToken cancellationToken);

    Task<AuthenticationResult> LoginAsync(LoginCustomerCommand command, CancellationToken cancellationToken);

    Task<string?> ChangePasswordAsync(ChangePasswordCommand command, CancellationToken cancellationToken);

    Task<string?> CreatePasswordRecoveryTokenAsync(string email, CancellationToken cancellationToken);

    Task<string?> ResetPasswordAsync(ResetPasswordCommand command, CancellationToken cancellationToken);
}

public sealed class AuthenticationService(
    ICustomerIdentityStore customerStore,
    IPasswordHasher passwordHasher,
    IPasswordPolicy passwordPolicy,
    IClock clock,
    IOptions<SecurityOptions> securityOptions,
    IEmailSender emailSender,
    IOptions<EmailOptions> emailOptions) : IAuthenticationService
{
    public async Task<AuthenticationResult> RegisterAsync(RegisterCustomerCommand command, CancellationToken cancellationToken)
    {
        var email = command.Email.Trim().ToLowerInvariant();
        if (await customerStore.FindByEmailAsync(email, cancellationToken) is not null)
            return new AuthenticationResult(null, CustomerIdentityErrors.EmailAlreadyExists);

        if (passwordPolicy.Validate(command.Password).Count > 0)
            return new AuthenticationResult(null, CustomerIdentityErrors.PasswordPolicy);

        var passwordResult = passwordHasher.HashPassword(command.Password);
        var customer = new Customer { Email = email, CreatedOnUtc = clock.UtcNow };
        var password = new CustomerPassword
        {
            Password = passwordResult.Hash,
            PasswordSalt = passwordResult.Salt,
            CreatedOnUtc = clock.UtcNow
        };

        customer.Id = await customerStore.CreateCustomerAsync(customer, password, cancellationToken);
        return new AuthenticationResult(customer, null);
    }

    public async Task<AuthenticationResult> LoginAsync(LoginCustomerCommand command, CancellationToken cancellationToken)
    {
        var email = command.Email.Trim().ToLowerInvariant();
        var customer = await customerStore.FindByEmailAsync(email, cancellationToken);
        if (customer is null)
            return new AuthenticationResult(null, CustomerIdentityErrors.InvalidCredentials);
        if (!customer.Active || customer.Deleted)
            return new AuthenticationResult(null, CustomerIdentityErrors.AccountInactive);
        if (customer.CannotLoginUntilDateUtc > clock.UtcNow)
            return new AuthenticationResult(null, CustomerIdentityErrors.AccountLocked);

        var password = await customerStore.GetLatestPasswordAsync(customer.Id, cancellationToken);
        if (password is null || !passwordHasher.Verify(command.Password, password))
        {
            customer.FailedLoginAttempts++;
            if (customer.FailedLoginAttempts >= securityOptions.Value.MaxFailedLoginAttempts)
            {
                customer.CannotLoginUntilDateUtc = clock.UtcNow.AddMinutes(securityOptions.Value.LockoutMinutes);
                customer.FailedLoginAttempts = 0;
            }
            await customerStore.UpdateCustomerAsync(customer, cancellationToken);
            return new AuthenticationResult(null, CustomerIdentityErrors.InvalidCredentials);
        }

        if (!customer.EmailVerified)
            return new AuthenticationResult(null, CustomerIdentityErrors.EmailNotVerified);

        customer.FailedLoginAttempts = 0;
        customer.CannotLoginUntilDateUtc = null;
        customer.RequireReLogin = false;
        customer.LastLoginDateUtc = clock.UtcNow;
        await customerStore.UpdateCustomerAsync(customer, cancellationToken);
        return new AuthenticationResult(customer, null);
    }

    public async Task<string?> ChangePasswordAsync(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var customer = await customerStore.FindByIdAsync(command.CustomerId, cancellationToken);
        var currentPassword = customer is null ? null : await customerStore.GetLatestPasswordAsync(customer.Id, cancellationToken);
        if (customer is null || currentPassword is null || !passwordHasher.Verify(command.CurrentPassword, currentPassword))
            return CustomerIdentityErrors.InvalidCredentials;

        if (passwordPolicy.Validate(command.NewPassword).Count > 0)
            return CustomerIdentityErrors.PasswordPolicy;

        var passwordHistory = await customerStore.GetPasswordHistoryAsync(
            customer.Id, securityOptions.Value.PasswordHistoryLimit, cancellationToken);
        if (passwordHistory.Any(previous => passwordHasher.Verify(command.NewPassword, previous)))
            return CustomerIdentityErrors.PasswordRecentlyUsed;

        var hashed = passwordHasher.HashPassword(command.NewPassword);
        var passwordRecord = new CustomerPassword
        {
            CustomerId = customer.Id, Password = hashed.Hash, PasswordSalt = hashed.Salt,
            CreatedOnUtc = clock.UtcNow
        };
        customer.RequireReLogin = true;
        await customerStore.ChangePasswordAsync(customer, passwordRecord, cancellationToken);
        return null;
    }

    public async Task<string?> CreatePasswordRecoveryTokenAsync(string email, CancellationToken cancellationToken)
    {
        var customer = await customerStore.FindByEmailAsync(email.Trim().ToLowerInvariant(), cancellationToken);
        if (customer is null)
            return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(token);
        await customerStore.CreateRecoveryTokenAsync(customer.Id, tokenHash,
            clock.UtcNow.AddMinutes(securityOptions.Value.RecoveryTokenLifetimeMinutes), cancellationToken);

        if (emailOptions.Value.Enabled)
        {
            var resetLink = BuildPasswordResetLink(token);
            var encodedResetLink = HtmlEncoder.Default.Encode(resetLink);
            await emailSender.SendEmailAsync(
                new EmailMessage(
                    customer.Email,
                    emailOptions.Value.PasswordRecoverySubject,
                    $"<p>We received a request to reset your Nomori Marketplace password.</p>" +
                    $"<p><a href=\"{encodedResetLink}\">Reset your password</a></p>" +
                    $"<p>This link expires in {securityOptions.Value.RecoveryTokenLifetimeMinutes} minutes and can only be used once.</p>",
                    $"Reset your Nomori Marketplace password: {resetLink}"),
                cancellationToken);
        }

        return token;
    }

    public async Task<string?> ResetPasswordAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(command.Token);
        var token = await customerStore.FindRecoveryTokenAsync(tokenHash, cancellationToken);
        if (token is null || token.Value.Used || token.Value.ExpiresOnUtc <= clock.UtcNow)
            return CustomerIdentityErrors.InvalidCredentials;

        var passwordErrors = passwordPolicy.Validate(command.NewPassword);
        if (passwordErrors.Count > 0)
            return CustomerIdentityErrors.PasswordPolicy;

        var passwordHistory = await customerStore.GetPasswordHistoryAsync(
            token.Value.CustomerId, securityOptions.Value.PasswordHistoryLimit, cancellationToken);
        if (passwordHistory.Any(previous => passwordHasher.Verify(command.NewPassword, previous)))
            return CustomerIdentityErrors.PasswordRecentlyUsed;

        var hashed = passwordHasher.HashPassword(command.NewPassword);
        var password = new CustomerPassword
        {
            CustomerId = token.Value.CustomerId, Password = hashed.Hash, PasswordSalt = hashed.Salt,
            CreatedOnUtc = clock.UtcNow
        };
        return await customerStore.ResetPasswordWithRecoveryTokenAsync(tokenHash, clock.UtcNow, password, cancellationToken)
            ? null
            : CustomerIdentityErrors.InvalidCredentials;
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private string BuildPasswordResetLink(string token)
    {
        var frontendBaseUrl = emailOptions.Value.FrontendBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(frontendBaseUrl))
            throw new InvalidOperationException("Email:FrontendBaseUrl must be configured when email delivery is enabled.");

        return $"{frontendBaseUrl}/auth/reset-password?token={Uri.EscapeDataString(token)}";
    }
}
