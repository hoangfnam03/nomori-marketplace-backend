using Nomori.Marketplace.Core.Domain.Customers;

namespace Nomori.Marketplace.Core.Customers;

public interface ICustomerIdentityStore
{
    Task<Customer?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task<Customer?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task<CustomerPassword?> GetLatestPasswordAsync(int customerId, CancellationToken cancellationToken);

    Task<int> CreateCustomerAsync(Customer customer, CustomerPassword password, CancellationToken cancellationToken);

    Task UpdateCustomerAsync(Customer customer, CancellationToken cancellationToken);
}