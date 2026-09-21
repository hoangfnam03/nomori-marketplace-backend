using Nomori.Marketplace.Core.Domain.Customers;

namespace Nomori.Marketplace.Core.Security;

public interface IAuthorizationManagementService
{
    Task<IReadOnlyList<CustomerRole>> GetActiveRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerRole>> GetCustomerRolesAsync(int customerId, CancellationToken cancellationToken);

    Task<bool> CustomerExistsAsync(int customerId, CancellationToken cancellationToken);

    Task<AuthorizationManagementResult> ReplaceCustomerRolesAsync(
        int actorCustomerId,
        int targetCustomerId,
        IReadOnlyCollection<string> roleSystemNames,
        CancellationToken cancellationToken);
}

public sealed record AuthorizationManagementResult(
    bool Succeeded,
    string? ErrorCode,
    IReadOnlyList<CustomerRole> Roles)
{
    public static AuthorizationManagementResult Failure(string errorCode) => new(false, errorCode, []);
}
