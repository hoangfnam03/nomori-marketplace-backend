using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Authentication;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Email;

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

    [Fact]
    public async Task RegisterRejectsPasswordThatDoesNotMeetPolicy()
    {
        var result = await CreateService(new InMemoryCustomerIdentityStore())
            .RegisterAsync(new RegisterCustomerCommand("user@example.test", "password"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(CustomerIdentityErrors.PasswordPolicy, result.ErrorCode);
    }

    [Fact]
    public async Task ChangePasswordRequiresReLoginAndRejectsPasswordReuse()
    {
        var store = new InMemoryCustomerIdentityStore();
        var service = CreateService(store);
        await service.RegisterAsync(new RegisterCustomerCommand("user@example.test", "Password!123"), CancellationToken.None);

        var changed = await service.ChangePasswordAsync(
            new ChangePasswordCommand(1, "Password!123", "Password!456"), CancellationToken.None);
        var reused = await service.ChangePasswordAsync(
            new ChangePasswordCommand(1, "Password!456", "Password!123"), CancellationToken.None);

        Assert.Null(changed);
        Assert.True(store.Customer!.RequireReLogin);
        Assert.Equal(CustomerIdentityErrors.PasswordRecentlyUsed, reused);
    }

    [Fact]
    public async Task PasswordRecoverySendsResetLinkWhenEmailIsEnabled()
    {
        var store = new InMemoryCustomerIdentityStore();
        await CreateService(store).RegisterAsync(
            new RegisterCustomerCommand("user@example.test", "Password!123"), CancellationToken.None);
        var sender = new CapturingEmailSender();
        var service = CreateService(store, sender, new EmailOptions
        {
            Enabled = true,
            FrontendBaseUrl = "https://marketplace.example.test"
        });

        var token = await service.CreatePasswordRecoveryTokenAsync("USER@example.test", CancellationToken.None);

        Assert.NotNull(token);
        Assert.NotNull(sender.Message);
        Assert.Equal("user@example.test", sender.Message!.ToAddress);
        Assert.Contains($"/auth/reset-password?token={Uri.EscapeDataString(token!)}", sender.Message.HtmlBody);
    }

    private static AuthenticationService CreateService(
        InMemoryCustomerIdentityStore store,
        IEmailSender? emailSender = null,
        EmailOptions? emailOptions = null) =>
        new(store, new PasswordHasher(), new PasswordPolicy(Options.Create(new SecurityOptions())), new FixedClock(), Options.Create(new SecurityOptions()),
            emailSender ?? new NoopEmailSender(), Options.Create(emailOptions ?? new EmailOptions()));

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public EmailMessage? Message { get; private set; }

        public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Message = message;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class InMemoryCustomerIdentityStore : ICustomerIdentityStore
    {
        private Customer? customer;
        private readonly List<CustomerPassword> passwords = [];
        public Customer? Customer => customer;
        public CustomerPassword? Password => passwords.FirstOrDefault();

        public Task<Customer?> FindByEmailAsync(string email, CancellationToken cancellationToken) => Task.FromResult(customer?.Email == email ? customer : null);
        public Task<Customer?> FindByIdAsync(int id, CancellationToken cancellationToken) => Task.FromResult(customer?.Id == id ? customer : null);
        public Task<CustomerPassword?> GetLatestPasswordAsync(int customerId, CancellationToken cancellationToken) => Task.FromResult(Password);

        public Task<IReadOnlyList<CustomerPassword>> GetPasswordHistoryAsync(int customerId, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerPassword>>(passwords.Take(take).ToArray());

        public Task<int> CreateCustomerAsync(Customer value, CustomerPassword password, CancellationToken cancellationToken)
        {
            value.Id = 1;
            customer = value;
            password.CustomerId = value.Id;
            passwords.Insert(0, password);
            return Task.FromResult(value.Id);
        }

        public Task UpdateCustomerAsync(Customer value, CancellationToken cancellationToken)
        {
            customer = value;
            return Task.CompletedTask;
        }

        public Task AddPasswordAsync(CustomerPassword password, CancellationToken cancellationToken)
        {
            passwords.Insert(0, password);
            return Task.CompletedTask;
        }

        public Task ChangePasswordAsync(Customer value, CustomerPassword password, CancellationToken cancellationToken)
        {
            customer = value;
            passwords.Insert(0, password);
            return Task.CompletedTask;
        }

        public Task CreateRecoveryTokenAsync(int customerId, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(int CustomerId, string TokenHash, DateTime ExpiresOnUtc, bool Used)?> FindRecoveryTokenAsync(string tokenHash, CancellationToken cancellationToken) => Task.FromResult<(int, string, DateTime, bool)?>(null);

        public Task MarkRecoveryTokenUsedAsync(string tokenHash, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ResetPasswordWithRecoveryTokenAsync(string tokenHash, DateTime nowUtc, CustomerPassword password, CancellationToken cancellationToken)
        {
            passwords.Insert(0, password);
            return Task.FromResult(true);
        }

        public Task<IReadOnlySet<string>> GetPermissionCodesAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
