using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Services.Security;

public sealed class PermissionService(ICustomerIdentityStore customerStore) : IPermissionService
{
    public async Task<bool> HasPermissionAsync(int customerId, string permissionCode, CancellationToken cancellationToken)
    {
        var permissions = await customerStore.GetPermissionCodesAsync(customerId, cancellationToken);
        return permissions.Contains(permissionCode);
    }

    public Task<IReadOnlySet<string>> GetPermissionsAsync(int customerId, CancellationToken cancellationToken) =>
        customerStore.GetPermissionCodesAsync(customerId, cancellationToken);
}