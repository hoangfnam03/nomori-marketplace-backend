using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Authentication;

public sealed record RegisterCustomerCommand(string Email, string Password);

public sealed record LoginCustomerCommand(string Email, string Password);

public sealed record AuthenticationResult(Customer? Customer, string? ErrorCode)
{
    public bool Succeeded => Customer is not null && ErrorCode is null;
}

public interface IAuthenticationService
{
    Task<AuthenticationResult> RegisterAsync(RegisterCustomerCommand command, CancellationToken cancellationToken);

    Task<AuthenticationResult> LoginAsync(LoginCustomerCommand command, CancellationToken cancellationToken);
}

public sealed class AuthenticationService(
    ICustomerIdentityStore customerStore,
    IPasswordHasher passwordHasher,
    IClock clock) : IAuthenticationService
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
            await customerStore.UpdateCustomerAsync(customer, cancellationToken);
            return new AuthenticationResult(null, CustomerIdentityErrors.InvalidCredentials);
        }

        customer.FailedLoginAttempts = 0;
        customer.CannotLoginUntilDateUtc = null;
        customer.LastLoginDateUtc = clock.UtcNow;
        await customerStore.UpdateCustomerAsync(customer, cancellationToken);
        return new AuthenticationResult(customer, null);
    }
}