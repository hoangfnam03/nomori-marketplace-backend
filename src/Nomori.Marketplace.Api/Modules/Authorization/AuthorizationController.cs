using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Authorization;

[ApiController]
[Route("api/v1/admin/authorization")]
public sealed class AuthorizationController(
    IAuthorizationManagementService authorizationService,
    ICurrentUser currentUser,
    IAuditLogService auditLog) : ControllerBase
{
    [HttpGet("roles")]
    [HasPermission(PermissionCodes.AdminRolesRead)]
    public async Task<IActionResult> GetRoles(CancellationToken cancellationToken)
    {
        var roles = await authorizationService.GetActiveRolesAsync(cancellationToken);
        return Ok(roles.Select(ToResponse));
    }

    [HttpGet("audit-logs")]
    [HasPermission(PermissionCodes.AdminAuditRead)]
    public async Task<IActionResult> GetAuditLogs([FromQuery] int take = 100, CancellationToken cancellationToken = default)
    {
        var logs = await auditLog.GetRecentAsync(take, cancellationToken);
        return Ok(logs);
    }

    [HttpGet("customers/{customerId:int}/roles")]
    [HasPermission(PermissionCodes.AdminRolesRead)]
    public async Task<IActionResult> GetCustomerRoles(int customerId, CancellationToken cancellationToken)
    {
        if (!await authorizationService.CustomerExistsAsync(customerId, cancellationToken))
            return NotFound(new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Customer not found", Detail = AuthorizationErrors.CustomerNotFound });

        var roles = await authorizationService.GetCustomerRolesAsync(customerId, cancellationToken);
        return Ok(new CustomerRolesResponse(customerId, roles.Select(ToResponse).ToArray()));
    }

    [HttpPut("customers/{customerId:int}/roles")]
    [HasPermission(PermissionCodes.AdminRolesManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplaceCustomerRoles(
        int customerId,
        ReplaceCustomerRolesRequest request,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(currentUser.Subject, out var actorCustomerId))
            return Unauthorized();

        var result = await authorizationService.ReplaceCustomerRolesAsync(
            actorCustomerId,
            customerId,
            request.RoleSystemNames,
            cancellationToken);
        if (result.Succeeded)
            return Ok(new CustomerRolesResponse(customerId, result.Roles.Select(ToResponse).ToArray()));

        return result.ErrorCode switch
        {
            AuthorizationErrors.CustomerNotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Customer not found",
                Detail = result.ErrorCode
            }),
            AuthorizationErrors.CannotRemoveOwnAdministratorRole => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Administrator role required",
                Detail = result.ErrorCode
            }),
            _ => BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Role assignment failed",
                Detail = result.ErrorCode
            })
        };
    }

    private static RoleResponse ToResponse(Nomori.Marketplace.Core.Domain.Customers.CustomerRole role) =>
        new(role.Id, role.Name, role.SystemName, role.IsSystemRole);
}

public sealed record RoleResponse(int Id, string Name, string SystemName, bool IsSystemRole);

public sealed record CustomerRolesResponse(int CustomerId, IReadOnlyList<RoleResponse> Roles);

public sealed record ReplaceCustomerRolesRequest(string[] RoleSystemNames);
