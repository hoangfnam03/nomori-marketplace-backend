using Nomori.Marketplace.Core.Domain.Customers;

namespace Nomori.Marketplace.Core.Customers;

public interface ICustomerIdentityStore
{
    Task<Customer?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task<Customer?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task<CustomerPassword?> GetLatestPasswordAsync(int customerId, CancellationToken cancellationToken);

    Task<int> CreateCustomerAsync(Customer customer, CustomerPassword password, CancellationToken cancellationToken);

    Task UpdateCustomerAsync(Customer customer, CancellationToken cancellationToken);

    Task AddPasswordAsync(CustomerPassword password, CancellationToken cancellationToken);

    Task CreateRecoveryTokenAsync(int customerId, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken);

    Task<(int CustomerId, string TokenHash, DateTime ExpiresOnUtc, bool Used)?> FindRecoveryTokenAsync(string tokenHash, CancellationToken cancellationToken);

    Task MarkRecoveryTokenUsedAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetPermissionCodesAsync(int customerId, CancellationToken cancellationToken);
}