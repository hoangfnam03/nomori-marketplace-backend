using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Services.Security;

namespace Nomori.Marketplace.Services.Tests;

public sealed class AuthorizationManagementServiceTests
{
    [Fact]
    public async Task AdministratorCannotRemoveOwnAdministratorRole()
    {
        var service = new AuthorizationManagementService(new InMemoryAuthorizationStore());

        var result = await service.ReplaceCustomerRolesAsync(1, 1, ["Registered"], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AuthorizationErrors.CannotRemoveOwnAdministratorRole, result.ErrorCode);
    }

    [Fact]
    public async Task RoleAssignmentNormalizesNamesAndReturnsAssignedRoles()
    {
        var store = new InMemoryAuthorizationStore();
        var service = new AuthorizationManagementService(store);

        var result = await service.ReplaceCustomerRolesAsync(1, 2, [" registered "], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Registered", result.Roles.Single().SystemName);
    }

    private sealed class InMemoryAuthorizationStore : IAuthorizationStore
    {
        private readonly List<CustomerRole> roles =
        [
            new CustomerRole { Id = 1, Name = "Administrators", SystemName = "Administrator", Active = true, IsSystemRole = true },
            new CustomerRole { Id = 2, Name = "Registered", SystemName = "Registered", Active = true, IsSystemRole = true }
        ];
        private readonly Dictionary<int, IReadOnlyCollection<string>> assignments = new()
        {
            [1] = ["Administrator"],
            [2] = ["Registered"]
        };

        public Task<IReadOnlyList<CustomerRole>> GetActiveRolesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CustomerRole>>(roles);

        public Task<IReadOnlyList<CustomerRole>> GetCustomerRolesAsync(int customerId, CancellationToken cancellationToken)
        {
            var assigned = assignments.GetValueOrDefault(customerId, []);
            return Task.FromResult<IReadOnlyList<CustomerRole>>(roles.Where(role => assigned.Contains(role.SystemName, StringComparer.OrdinalIgnoreCase)).ToArray());
        }

        public Task<bool> CustomerExistsAsync(int customerId, CancellationToken cancellationToken) => Task.FromResult(assignments.ContainsKey(customerId));

        public Task ReplaceCustomerRolesAsync(int customerId, IReadOnlyCollection<string> roleSystemNames, CancellationToken cancellationToken)
        {
            assignments[customerId] = roleSystemNames;
            return Task.CompletedTask;
        }
    }
}
