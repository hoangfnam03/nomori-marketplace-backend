using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Authentication;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Services.Tests;

public sealed class AuthenticationServiceTests
{
    [Fact]
    public async Task RegisterCreatesHashedPasswordAndNormalizedEmail()
    {
        var store = new InMemoryCustomerIdentityStore();
        var service = CreateService(store);

        var result = await service.RegisterAsync(new RegisterCustomerCommand(" User@Example.Test ", "Password!123"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("user@example.test", result.Customer!.Email);
        Assert.NotNull(store.Password);
        Assert.NotEqual("Password!123", store.Password!.Password);
    }

    [Fact]
    public async Task LoginRejectsWrongPassword()
    {
        var store = new InMemoryCustomerIdentityStore();
        var service = CreateService(store);
        await service.RegisterAsync(new RegisterCustomerCommand("user@example.test", "Password!123"), CancellationToken.None);

        var result = await service.LoginAsync(new LoginCustomerCommand("user@example.test", "wrong"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(CustomerIdentityErrors.InvalidCredentials, result.ErrorCode);
    }

    private static AuthenticationService CreateService(InMemoryCustomerIdentityStore store) =>
        new(store, new PasswordHasher(), new FixedClock(), Options.Create(new SecurityOptions()));

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class InMemoryCustomerIdentityStore : ICustomerIdentityStore
    {
        private Customer? customer;
        public CustomerPassword? Password { get; private set; }

        public Task<Customer?> FindByEmailAsync(string email, CancellationToken cancellationToken) => Task.FromResult(customer?.Email == email ? customer : null);
        public Task<Customer?> FindByIdAsync(int id, CancellationToken cancellationToken) => Task.FromResult(customer?.Id == id ? customer : null);
        public Task<CustomerPassword?> GetLatestPasswordAsync(int customerId, CancellationToken cancellationToken) => Task.FromResult(Password);

        public Task<int> CreateCustomerAsync(Customer value, CustomerPassword password, CancellationToken cancellationToken)
        {
            value.Id = 1;
            customer = value;
            password.CustomerId = value.Id;
            Password = password;
            return Task.FromResult(value.Id);
        }

        public Task UpdateCustomerAsync(Customer value, CancellationToken cancellationToken)
        {
            customer = value;
            return Task.CompletedTask;
        }

        public Task AddPasswordAsync(CustomerPassword password, CancellationToken cancellationToken)
        {
            Password = password;
            return Task.CompletedTask;
        }

        public Task CreateRecoveryTokenAsync(int customerId, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(int CustomerId, string TokenHash, DateTime ExpiresOnUtc, bool Used)?> FindRecoveryTokenAsync(string tokenHash, CancellationToken cancellationToken) => Task.FromResult<(int, string, DateTime, bool)?>(null);

        public Task MarkRecoveryTokenUsedAsync(string tokenHash, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlySet<string>> GetPermissionCodesAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}