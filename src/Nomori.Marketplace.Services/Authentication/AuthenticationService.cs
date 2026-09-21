using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Time;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Security;
using System.Security.Cryptography;
using System.Text;

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
    IClock clock,
    IOptions<SecurityOptions> securityOptions) : IAuthenticationService
{
    public async Task<AuthenticationResult> RegisterAsync(RegisterCustomerCommand command, CancellationToken cancellationToken)
    {
        var email = command.Email.Trim().ToLowerInvariant();
        if (await customerStore.FindByEmailAsync(email, cancellationToken) is not null)
            return new AuthenticationResult(null, CustomerIdentityErrors.EmailAlreadyExists);

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

        customer.FailedLoginAttempts = 0;
        customer.CannotLoginUntilDateUtc = null;
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

        var hashed = passwordHasher.HashPassword(command.NewPassword);
        await customerStore.AddPasswordAsync(new CustomerPassword
        {
            CustomerId = customer.Id, Password = hashed.Hash, PasswordSalt = hashed.Salt,
            CreatedOnUtc = clock.UtcNow
        }, cancellationToken);
        customer.RequireReLogin = true;
        await customerStore.UpdateCustomerAsync(customer, cancellationToken);
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
        return token;
    }

    public async Task<string?> ResetPasswordAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(command.Token);
        var token = await customerStore.FindRecoveryTokenAsync(tokenHash, cancellationToken);
        if (token is null || token.Value.Used || token.Value.ExpiresOnUtc <= clock.UtcNow)
            return CustomerIdentityErrors.InvalidCredentials;

        var hashed = passwordHasher.HashPassword(command.NewPassword);
        await customerStore.AddPasswordAsync(new CustomerPassword
        {
            CustomerId = token.Value.CustomerId, Password = hashed.Hash, PasswordSalt = hashed.Salt,
            CreatedOnUtc = clock.UtcNow
        }, cancellationToken);
        await customerStore.MarkRecoveryTokenUsedAsync(tokenHash, cancellationToken);
        return null;
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}