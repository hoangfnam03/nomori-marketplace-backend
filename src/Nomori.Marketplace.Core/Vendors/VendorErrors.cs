namespace Nomori.Marketplace.Core.Vendors;

/// <summary>
/// Stable error codes returned in <c>ProblemDetails.detail</c>. <see cref="NotFound"/> and <see cref="Forbidden"/>
/// are mapped to 404 and 403; every other code is a 409 business-rule conflict.
/// </summary>
public static class VendorErrors
{
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";

    public const string ApplicationEmailNotVerified = "vendor_application.email_not_verified";
    public const string ApplicationAlreadyVendor = "vendor_application.already_vendor";
    public const string ApplicationAlreadyPending = "vendor_application.already_pending";
    public const string ApplicationNotPending = "vendor_application.not_pending";
    public const string ApplicantAlreadyVendor = "vendor_application.applicant_already_vendor";

    public const string MemberEmailAlreadyExists = "vendor_member.email_already_exists";
    public const string MemberLimitReached = "vendor_member.limit_reached";
    public const string MemberAlreadyActive = "vendor_member.already_active";
    public const string MemberLastMember = "vendor_member.last_member";
}

/// <summary>Role that marks an account as a shop member. Kept in sync with <c>Customer.VendorId</c>.</summary>
public static class VendorRoles
{
    public const string Vendors = "Vendors";
}

public sealed class VendorOptions
{
    public const string SectionName = "Vendor";

    public int MaxMembersPerVendor { get; init; } = 20;
}

/// <summary>Who is calling, computed once per request. A caller is a member of at most one vendor.</summary>
public sealed record VendorCaller(int? CustomerId, bool IsAdmin, int? MemberVendorId)
{
    public static VendorCaller Anonymous { get; } = new(null, false, null);

    public bool IsAuthenticated => CustomerId is not null;

    public bool IsMemberOf(int vendorId) => MemberVendorId == vendorId;
}

public interface IVendorAccessContext
{
    Task<VendorCaller> GetCallerAsync(CancellationToken cancellationToken);
}
