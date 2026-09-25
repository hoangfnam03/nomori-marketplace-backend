namespace Nomori.Marketplace.Core.Security;

/// <summary>
/// Contains stable permission codes. Feature modules add their own codes near their feature boundary.
/// </summary>
public static class PermissionCodes
{
    public const string Authenticated = "auth.authenticated";

    public const string PermissionsRead = "auth.permissions.read";

    public const string AdminAccess = "admin.access";

    public const string AdminRolesRead = "admin.roles.read";

    public const string AdminRolesManage = "admin.roles.manage";

    public const string AdminAuditRead = "admin.audit.read";

    public const string CustomerProfileRead = "customer.profile.read";

    public const string CustomerProfileManage = "customer.profile.manage";

    public const string CustomerAddressManage = "customer.address.manage";
    public const string CustomerAttributesManage = "customer.attributes.manage";
    public const string CustomerEmailChange = "customer.email.change";

    public const string CatalogManage = "catalog.manage";

    public const string VendorManage = "vendor.manage";
    public const string VendorPortal = "vendor.portal";
}
