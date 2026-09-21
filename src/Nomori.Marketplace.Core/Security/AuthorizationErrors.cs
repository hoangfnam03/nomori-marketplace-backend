namespace Nomori.Marketplace.Core.Security;

public static class AuthorizationErrors
{
    public const string CustomerNotFound = "authorization.customer_not_found";

    public const string RoleNotFound = "authorization.role_not_found";

    public const string EmptyRoleSelection = "authorization.empty_role_selection";

    public const string CannotRemoveOwnAdministratorRole = "authorization.cannot_remove_own_administrator_role";
}
