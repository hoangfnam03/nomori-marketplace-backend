namespace Nomori.Marketplace.Core.Vendors;

public sealed class Vendor
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int PictureId { get; set; }
    public int AddressId { get; set; }
    public string? AdminComment { get; set; }
    public bool Active { get; set; }
    public bool Deleted { get; set; }
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

public sealed record VendorQuery(
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    bool? Active = null);

public sealed record CreateVendorCommand(
    string Name,
    string Email,
    string? Description,
    string? AdminComment,
    bool Active,
    int DisplayOrder);

public sealed record UpdateVendorCommand(
    int Id,
    string Name,
    string Email,
    string? Description,
    string? AdminComment,
    bool Active,
    int DisplayOrder);

public sealed record VendorResult<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

public static class VendorResult
{
    public static VendorResult<T> Success<T>(T value) =>
        new(value, new Dictionary<string, string[]>());

    public static VendorResult<T> Failure<T>(string field, string message) =>
        new(default, new Dictionary<string, string[]> { [field] = [message] });

    public static VendorResult<T> Failure<T>(IReadOnlyDictionary<string, string[]> errors) =>
        new(default, errors);
}

public interface IVendorStore
{
    Task<Vendor?> GetAsync(int id, CancellationToken cancellationToken);
    Task<Vendor?> GetByCustomerIdAsync(int customerId, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Vendor> Items, int TotalCount)> GetPagedAsync(VendorQuery query, CancellationToken cancellationToken);
    Task<int> InsertAsync(Vendor vendor, CancellationToken cancellationToken);
    Task UpdateAsync(Vendor vendor, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
    Task SetCustomerVendorAsync(int customerId, int? vendorId, CancellationToken cancellationToken);

    // Notes
    Task<(IReadOnlyList<VendorNote> Items, int TotalCount)> GetNotesPagedAsync(int vendorId, int page, int pageSize, CancellationToken cancellationToken);
    Task<int> InsertNoteAsync(VendorNote note, CancellationToken cancellationToken);
    Task<bool> DeleteNoteAsync(int id, CancellationToken cancellationToken);
}

public interface IVendorService
{
    Task<Vendor?> GetAsync(int id, CancellationToken cancellationToken);
    Task<Vendor?> GetCurrentVendorAsync(int customerId, CancellationToken cancellationToken);
    Task<Core.Catalog.PagedResult<Vendor>> GetListAsync(VendorQuery query, CancellationToken cancellationToken);
    Task<VendorResult<Vendor>> CreateAsync(CreateVendorCommand command, CancellationToken cancellationToken);
    Task<VendorResult<Vendor>> UpdateAsync(UpdateVendorCommand command, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
    Task<bool> AssignCustomerAsync(int vendorId, int customerId, CancellationToken cancellationToken);
    Task UnassignCustomerAsync(int customerId, CancellationToken cancellationToken);

    // Notes
    Task<Core.Catalog.PagedResult<VendorNote>> GetNotesAsync(int vendorId, int page, int pageSize, CancellationToken cancellationToken);
    Task<VendorNote> AddNoteAsync(int vendorId, string note, CancellationToken cancellationToken);
    Task<bool> DeleteNoteAsync(int id, CancellationToken cancellationToken);
}
