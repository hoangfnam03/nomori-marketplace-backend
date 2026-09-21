using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Web.Framework.Security;

public static class PermissionPolicy
{
    public const string Prefix = "Nomori.Permission:";

    public static string For(string permissionCode) => $"{Prefix}{permissionCode}";
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public HasPermissionAttribute(string permissionCode)
    {
        PermissionCode = permissionCode;
    }

    public string PermissionCode
    {
        get => Policy is { } policy && policy.StartsWith(PermissionPolicy.Prefix, StringComparison.Ordinal)
            ? policy[PermissionPolicy.Prefix.Length..]
            : string.Empty;
        set => Policy = PermissionPolicy.For(value);
    }
}

public sealed record PermissionRequirement(string PermissionCode) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationPolicyProvider(
    IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(PermissionPolicy.Prefix, StringComparison.Ordinal))
            return base.GetPolicyAsync(policyName);

        var permissionCode = policyName[PermissionPolicy.Prefix.Length..];
        if (string.IsNullOrWhiteSpace(permissionCode))
            return Task.FromResult<AuthorizationPolicy?>(null);

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permissionCode))
            .Build();
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

public sealed class PermissionAuthorizationHandler(IPermissionService permissionService)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(subject, out var customerId))
            return;

        if (await permissionService.HasPermissionAsync(customerId, requirement.PermissionCode, CancellationToken.None))
            context.Succeed(requirement);
    }
}
