using Nomori.Marketplace.Core.Domain.Customers;

namespace Nomori.Marketplace.Core.Security;

public interface IAuthorizationStore
{
    Task<IReadOnlyList<CustomerRole>> GetActiveRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerRole>> GetCustomerRolesAsync(int customerId, CancellationToken cancellationToken);

    Task<bool> CustomerExistsAsync(int customerId, CancellationToken cancellationToken);

    Task ReplaceCustomerRolesAsync(int customerId, IReadOnlyCollection<string> roleSystemNames, CancellationToken cancellationToken);
}
