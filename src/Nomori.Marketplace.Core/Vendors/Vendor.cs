namespace Nomori.Marketplace.Core.Vendors;

public sealed class Vendor
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Contact and business details. Shown to administrators and the shop's members only, never on the storefront.</summary>
    public string? PhoneNumber { get; set; }
    public string? TaxCode { get; set; }
    public string? BusinessAddress { get; set; }

    public int PictureId { get; set; }
    public int AddressId { get; set; }
    public string? AdminComment { get; set; }
    public bool Active { get; set; }
    public bool Deleted { get; set; }

    /// <summary>The single Nomori-owned shop that owns platform products. It cannot be deleted or deactivated.</summary>
    public bool IsPlatformShop { get; set; }

    public int DisplayOrder { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public sealed class VendorNote
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime CreatedOnUtc { get; set; }
}

/// <param name="IncludeInactiveVendorId">
/// When <paramref name="Active"/> is <c>true</c>, also returns this vendor even if it is inactive (a member's own shop).
/// </param>
public sealed record VendorQuery(
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    bool? Active = null,
    int? IncludeInactiveVendorId = null);

/// <summary>
/// Replaces the shop profile. The last three fields are for administrators only: null keeps the current value,
/// and a shop member who sends any of them is refused.
/// </summary>
public sealed record UpdateVendorCommand(
    int Id,
    string Name,
    string Email,
    string? PhoneNumber,
    string? Description,
    string? TaxCode,
    string? BusinessAddress,
    int? PictureId = null,
    string? AdminComment = null,
    bool? Active = null,
    int? DisplayOrder = null);

/// <summary>
/// Outcome of a vendor operation. <see cref="Errors"/> are field validation errors (400);
/// <see cref="ErrorCode"/> is a <see cref="VendorErrors"/> code (403, 404 or 409).
/// </summary>
public sealed record VendorResult<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors, string? ErrorCode = null)
{
    public bool Succeeded => Errors.Count == 0 && ErrorCode is null;
}

public static class VendorResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    public static VendorResult<T> Success<T>(T value) => new(value, NoErrors);

    public static VendorResult<T> Failure<T>(string field, string message) =>
        new(default, new Dictionary<string, string[]> { [field] = [message] });

    public static VendorResult<T> Failure<T>(IReadOnlyDictionary<string, string[]> errors) =>
        new(default, errors);

    public static VendorResult<T> Error<T>(string errorCode) => new(default, NoErrors, errorCode);
}

public interface IVendorStore
{
    Task<Vendor?> GetAsync(int id, CancellationToken cancellationToken);
    Task<Vendor?> GetByCustomerIdAsync(int customerId, CancellationToken cancellationToken);
    Task<Vendor?> GetPlatformShopAsync(CancellationToken cancellationToken);
    Task<(IReadOnlyList<Vendor> Items, int TotalCount)> GetPagedAsync(VendorQuery query, CancellationToken cancellationToken);
    Task UpdateAsync(Vendor vendor, CancellationToken cancellationToken);

    /// <summary>True when another live vendor already uses this name, ignoring case and surrounding spaces.</summary>
    Task<bool> NameExistsAsync(string name, int excludeVendorId, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes the vendor and, in the same transaction, unlinks every member, removes their Vendors role
    /// and sets RequireReLogin. Returns the former member ids, or null when the vendor does not exist.
    /// </summary>
    Task<IReadOnlyList<int>?> DeleteAsync(int id, DateTime nowUtc, CancellationToken cancellationToken);

    // Notes
    Task<(IReadOnlyList<VendorNote> Items, int TotalCount)> GetNotesPagedAsync(int vendorId, int page, int pageSize, CancellationToken cancellationToken);
    Task<int> InsertNoteAsync(VendorNote note, CancellationToken cancellationToken);
    Task<bool> DeleteNoteAsync(int vendorId, int noteId, CancellationToken cancellationToken);
}

public interface IVendorService
{
    Task<Vendor?> GetAsync(int id, CancellationToken cancellationToken);
    Task<Vendor?> GetCurrentVendorAsync(int customerId, CancellationToken cancellationToken);
    Task<Catalog.PagedResult<Vendor>> GetListAsync(VendorQuery query, CancellationToken cancellationToken);
    /// <summary>Administrators update any vendor; a member updates the profile of their own shop. Others get not_found.</summary>
    Task<VendorResult<Vendor>> UpdateAsync(UpdateVendorCommand command, VendorCaller caller, CancellationToken cancellationToken);
    Task<VendorResult<bool>> DeleteAsync(int id, int actorCustomerId, CancellationToken cancellationToken);
    Task<Vendor?> GetPlatformShopAsync(CancellationToken cancellationToken);

    // Notes
    Task<Catalog.PagedResult<VendorNote>> GetNotesAsync(int vendorId, int page, int pageSize, CancellationToken cancellationToken);
    Task<VendorNote> AddNoteAsync(int vendorId, string note, CancellationToken cancellationToken);
    Task<bool> DeleteNoteAsync(int vendorId, int noteId, CancellationToken cancellationToken);
}
