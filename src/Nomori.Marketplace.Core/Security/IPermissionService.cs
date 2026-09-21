namespace Nomori.Marketplace.Core.Security;

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(int customerId, string permissionCode, CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetPermissionsAsync(int customerId, CancellationToken cancellationToken);
}