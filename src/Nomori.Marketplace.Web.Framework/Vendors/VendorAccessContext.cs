using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Web.Framework.Vendors;

/// <summary>
/// Works out, once per request, whether the caller is an administrator and which vendor (if any) they are a member of.
/// Customer id and vendor id always come from the session, never from request data.
/// </summary>
public sealed class VendorAccessContext(
    ICurrentUser currentUser,
    IPermissionService permissionService,
    IVendorStore vendorStore) : IVendorAccessContext
{
    private Task<VendorCaller>? caller;

    public Task<VendorCaller> GetCallerAsync(CancellationToken cancellationToken) =>
        caller ??= ResolveAsync(cancellationToken);

    private async Task<VendorCaller> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || !int.TryParse(currentUser.Subject, out var customerId))
            return VendorCaller.Anonymous;

        var isAdmin = await permissionService.HasPermissionAsync(customerId, PermissionCodes.VendorManage, cancellationToken);

        int? memberVendorId = null;
        if (await permissionService.HasPermissionAsync(customerId, PermissionCodes.VendorPortal, cancellationToken))
            memberVendorId = (await vendorStore.GetByCustomerIdAsync(customerId, cancellationToken))?.Id;

        return new VendorCaller(customerId, isAdmin, memberVendorId);
    }
}
