using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Vendors;

public sealed class VendorService(IVendorStore vendorStore, IClock clock) : IVendorService
{
    public Task<Vendor?> GetAsync(int id, CancellationToken cancellationToken) =>
        vendorStore.GetAsync(id, cancellationToken);

    public Task<Vendor?> GetCurrentVendorAsync(int customerId, CancellationToken cancellationToken) =>
        vendorStore.GetByCustomerIdAsync(customerId, cancellationToken);

    public async Task<PagedResult<Vendor>> GetListAsync(VendorQuery query, CancellationToken cancellationToken)
    {
        var (items, total) = await vendorStore.GetPagedAsync(query, cancellationToken);
        return new PagedResult<Vendor>(items, total, query.Page, query.PageSize);
    }

    public async Task<VendorResult<Vendor>> CreateAsync(CreateVendorCommand command, CancellationToken cancellationToken)
    {
        var errors = Validate(command.Name, command.Email);
        if (errors.Count > 0) return VendorResult.Failure<Vendor>(errors);

        var now = clock.UtcNow;
        var vendor = new Vendor
        {
            Name = command.Name.Trim(),
            Email = command.Email.Trim().ToLowerInvariant(),
            Description = NullIfBlank(command.Description),
            AdminComment = NullIfBlank(command.AdminComment),
            Active = command.Active,
            DisplayOrder = command.DisplayOrder,
            CreatedOnUtc = now,
            UpdatedOnUtc = now
        };
        vendor.Id = await vendorStore.InsertAsync(vendor, cancellationToken);
        return VendorResult.Success(vendor);
    }

    public async Task<VendorResult<Vendor>> UpdateAsync(UpdateVendorCommand command, CancellationToken cancellationToken)
    {
        var existing = await vendorStore.GetAsync(command.Id, cancellationToken);
        if (existing is null) return VendorResult.Failure<Vendor>("id", "Vendor not found.");

        var errors = Validate(command.Name, command.Email);
        if (errors.Count > 0) return VendorResult.Failure<Vendor>(errors);

        existing.Name = command.Name.Trim();
        existing.Email = command.Email.Trim().ToLowerInvariant();
        existing.Description = NullIfBlank(command.Description);
        existing.AdminComment = NullIfBlank(command.AdminComment);
        existing.Active = command.Active;
        existing.DisplayOrder = command.DisplayOrder;
        existing.UpdatedOnUtc = clock.UtcNow;
        await vendorStore.UpdateAsync(existing, cancellationToken);
        return VendorResult.Success(existing);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var existing = await vendorStore.GetAsync(id, cancellationToken);
        if (existing is null) return false;
        await vendorStore.DeleteAsync(id, cancellationToken);
        return true;
    }

    public async Task<bool> AssignCustomerAsync(int vendorId, int customerId, CancellationToken cancellationToken)
    {
        var vendor = await vendorStore.GetAsync(vendorId, cancellationToken);
        if (vendor is null) return false;
        await vendorStore.SetCustomerVendorAsync(customerId, vendorId, cancellationToken);
        return true;
    }

    public Task UnassignCustomerAsync(int customerId, CancellationToken cancellationToken) =>
        vendorStore.SetCustomerVendorAsync(customerId, null, cancellationToken);

    public async Task<PagedResult<VendorNote>> GetNotesAsync(int vendorId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (items, total) = await vendorStore.GetNotesPagedAsync(vendorId, page, pageSize, cancellationToken);
        return new PagedResult<VendorNote>(items, total, page, pageSize);
    }

    public async Task<VendorNote> AddNoteAsync(int vendorId, string note, CancellationToken cancellationToken)
    {
        var vendorNote = new VendorNote { VendorId = vendorId, Note = note.Trim(), CreatedOnUtc = clock.UtcNow };
        vendorNote.Id = await vendorStore.InsertNoteAsync(vendorNote, cancellationToken);
        return vendorNote;
    }

    public Task<bool> DeleteNoteAsync(int id, CancellationToken cancellationToken) =>
        vendorStore.DeleteNoteAsync(id, cancellationToken);

    private static Dictionary<string, string[]> Validate(string name, string email)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Vendor name is required."];
        else if (name.Length > 400) errors["name"] = ["Vendor name cannot exceed 400 characters."];
        if (string.IsNullOrWhiteSpace(email)) errors["email"] = ["Vendor email is required."];
        else if (email.Length > 320) errors["email"] = ["Vendor email cannot exceed 320 characters."];
        return errors;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
