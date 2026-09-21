using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Services.Security;

public sealed class AuthorizationManagementService(
    IAuthorizationStore authorizationStore,
    IAuditLogService? auditLog = null) : IAuthorizationManagementService
{
    public Task<IReadOnlyList<CustomerRole>> GetActiveRolesAsync(CancellationToken cancellationToken) =>
        authorizationStore.GetActiveRolesAsync(cancellationToken);

    public Task<IReadOnlyList<CustomerRole>> GetCustomerRolesAsync(int customerId, CancellationToken cancellationToken) =>
        authorizationStore.GetCustomerRolesAsync(customerId, cancellationToken);

    public Task<bool> CustomerExistsAsync(int customerId, CancellationToken cancellationToken) =>
        authorizationStore.CustomerExistsAsync(customerId, cancellationToken);

    public async Task<AuthorizationManagementResult> ReplaceCustomerRolesAsync(
        int actorCustomerId,
        int targetCustomerId,
        IReadOnlyCollection<string> roleSystemNames,
        CancellationToken cancellationToken)
    {
        if (!await authorizationStore.CustomerExistsAsync(targetCustomerId, cancellationToken))
            return AuthorizationManagementResult.Failure(AuthorizationErrors.CustomerNotFound);

        var normalizedNames = roleSystemNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedNames.Length == 0)
            return AuthorizationManagementResult.Failure(AuthorizationErrors.EmptyRoleSelection);

        var activeRoles = await authorizationStore.GetActiveRolesAsync(cancellationToken);
        var activeRoleNames = activeRoles.Select(role => role.SystemName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedNames.Any(name => !activeRoleNames.Contains(name)))
            return AuthorizationManagementResult.Failure(AuthorizationErrors.RoleNotFound);

        if (actorCustomerId == targetCustomerId
            && !normalizedNames.Contains("Administrator", StringComparer.OrdinalIgnoreCase))
            return AuthorizationManagementResult.Failure(AuthorizationErrors.CannotRemoveOwnAdministratorRole);

        await authorizationStore.ReplaceCustomerRolesAsync(targetCustomerId, normalizedNames, cancellationToken);
        if (auditLog is not null)
        {
            await auditLog.WriteAsync(
                "auth.roles_replaced",
                actorCustomerId,
                targetCustomerId,
                "Customer",
                targetCustomerId,
                details: new { roles = normalizedNames },
                cancellationToken: cancellationToken);
        }
        return new AuthorizationManagementResult(
            true,
            null,
            await authorizationStore.GetCustomerRolesAsync(targetCustomerId, cancellationToken));
    }
}
