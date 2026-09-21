using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Tests;

public sealed class PermissionAuthorizationTests
{
    [Fact]
    public async Task PermissionPolicySucceedsForCustomerWithPermission()
    {
        var handler = new PermissionAuthorizationHandler(new StubPermissionService(true));
        var context = new AuthorizationHandlerContext(
            [new PermissionRequirement(PermissionCodes.PermissionsRead)],
            new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "42")
            ], "test")),
            null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task PermissionPolicyFailsForCustomerWithoutPermission()
    {
        var handler = new PermissionAuthorizationHandler(new StubPermissionService(false));
        var context = new AuthorizationHandlerContext(
            [new PermissionRequirement(PermissionCodes.PermissionsRead)],
            new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "42")
            ], "test")),
            null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task PermissionPolicyProviderBuildsAuthenticatedPermissionPolicy()
    {
        var provider = new PermissionAuthorizationPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync(PermissionPolicy.For(PermissionCodes.PermissionsRead));

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, requirement => requirement is PermissionRequirement permission && permission.PermissionCode == PermissionCodes.PermissionsRead);
        Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    private sealed class StubPermissionService(bool allowed) : IPermissionService
    {
        public Task<bool> HasPermissionAsync(int customerId, string permissionCode, CancellationToken cancellationToken) =>
            Task.FromResult(allowed);

        public Task<IReadOnlySet<string>> GetPermissionsAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }
}
