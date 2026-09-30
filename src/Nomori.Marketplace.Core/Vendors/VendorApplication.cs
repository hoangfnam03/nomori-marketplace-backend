namespace Nomori.Marketplace.Core.Vendors;

public enum VendorApplicationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3
}

public sealed class VendorApplication
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string ShopName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? TaxCode { get; set; }
    public string? BusinessAddress { get; set; }
    public VendorApplicationStatus Status { get; set; }
    public string? RejectReason { get; set; }
    public int? ReviewedByCustomerId { get; set; }
    public DateTime? ReviewedOnUtc { get; set; }
    public int? VendorId { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }

    /// <summary>Applicant account details, filled by reads only.</summary>
    public string? CustomerEmail { get; set; }
    public string? CustomerUsername { get; set; }
}

public sealed record VendorApplicationQuery(
    int Page = 1,
    int PageSize = 20,
    VendorApplicationStatus? Status = null,
    string? Search = null,
    int? CustomerId = null,
    bool OldestFirst = false);

public sealed record SubmitVendorApplicationCommand(
    string ShopName,
    string Email,
    string PhoneNumber,
    string? Description,
    string? TaxCode,
    string? BusinessAddress);

public sealed record UpdateVendorApplicationCommand(
    string ShopName,
    string Email,
    string PhoneNumber,
    string? Description,
    string? TaxCode,
    string? BusinessAddress);

public sealed record ChangeVendorApplicationStatusCommand(
    VendorApplicationStatus? Status,
    string? Reason,
    string? ShopName,
    string? AdminComment);

public enum ApproveApplicationOutcome
{
    Approved,
    NotPending,
    ApplicantAlreadyVendor
}

public sealed record ApproveApplicationResult(ApproveApplicationOutcome Outcome, int VendorId = 0);

public interface IVendorApplicationStore
{
    Task<VendorApplication?> GetAsync(int id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<VendorApplication> Items, int TotalCount)> GetPagedAsync(VendorApplicationQuery query, CancellationToken cancellationToken);

    /// <summary>Case-insensitive check against non-deleted vendors and pending applications.</summary>
    Task<bool> ShopNameExistsAsync(string shopName, int? excludeApplicationId, CancellationToken cancellationToken);

    /// <summary>Returns the new id, or null when the customer already has a pending application.</summary>
    Task<int?> InsertAsync(VendorApplication application, CancellationToken cancellationToken);

    /// <summary>Updates the editable fields only while the application is pending. Returns false otherwise.</summary>
    Task<bool> UpdateContentAsync(VendorApplication application, CancellationToken cancellationToken);

    /// <summary>Moves a pending application to rejected or cancelled. Returns false if it was not pending.</summary>
    Task<bool> CloseAsync(int id, VendorApplicationStatus status, string? rejectReason, int? reviewedByCustomerId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Creates the vendor, links the applicant, grants the Vendors role and marks the application approved in one transaction.</summary>
    Task<ApproveApplicationResult> ApproveAsync(int id, string shopName, string? adminComment, int reviewedByCustomerId, DateTime nowUtc, CancellationToken cancellationToken);
}

public interface IVendorApplicationService
{
    Task<VendorResult<VendorApplication>> GetAsync(int id, VendorCaller caller, CancellationToken cancellationToken);

    Task<Catalog.PagedResult<VendorApplication>> GetListAsync(VendorApplicationQuery query, VendorCaller caller, CancellationToken cancellationToken);

    Task<VendorResult<VendorApplication>> SubmitAsync(SubmitVendorApplicationCommand command, VendorCaller caller, CancellationToken cancellationToken);

    Task<VendorResult<VendorApplication>> UpdateAsync(int id, UpdateVendorApplicationCommand command, VendorCaller caller, CancellationToken cancellationToken);

    Task<VendorResult<VendorApplication>> ChangeStatusAsync(int id, ChangeVendorApplicationStatusCommand command, VendorCaller caller, CancellationToken cancellationToken);
}
