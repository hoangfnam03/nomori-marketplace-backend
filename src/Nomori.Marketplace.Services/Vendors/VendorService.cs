using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Services.Media;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Vendors;

public sealed class VendorService(IVendorStore vendorStore, IMediaStore mediaStore, IAuditLogService auditLog, IClock clock) : IVendorService
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

    public Task<Vendor?> GetPlatformShopAsync(CancellationToken cancellationToken) =>
        vendorStore.GetPlatformShopAsync(cancellationToken);

    public async Task<VendorResult<Vendor>> UpdateAsync(UpdateVendorCommand command, CancellationToken cancellationToken)
    {
        var existing = await vendorStore.GetAsync(command.Id, cancellationToken);
        if (existing is null) return VendorResult.Error<Vendor>(VendorErrors.NotFound);

        var errors = Validate(command.Name, command.Email);
        if (existing.IsPlatformShop && !command.Active)
            errors["active"] = ["The platform shop cannot be deactivated."];
        if (command.PictureId is { } pictureId && pictureId != existing.PictureId)
            await MediaAttachment.ValidateAsync(mediaStore, pictureId, MediaPurpose.VendorLogo, existing.Id, errors, cancellationToken);
        if (errors.Count > 0) return VendorResult.Failure<Vendor>(errors);

        existing.Name = command.Name.Trim();
        existing.Email = command.Email.Trim().ToLowerInvariant();
        existing.Description = VendorValidation.NullIfBlank(command.Description);
        existing.AdminComment = VendorValidation.NullIfBlank(command.AdminComment);
        existing.Active = command.Active;
        existing.DisplayOrder = command.DisplayOrder;
        if (command.PictureId is { } newPicture) existing.PictureId = newPicture;
        existing.UpdatedOnUtc = clock.UtcNow;
        await vendorStore.UpdateAsync(existing, cancellationToken);
        return VendorResult.Success(existing);
    }

    public async Task<VendorResult<bool>> DeleteAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var vendor = await vendorStore.GetAsync(id, cancellationToken);
        if (vendor is null) return VendorResult.Error<bool>(VendorErrors.NotFound);
        if (vendor.IsPlatformShop) return VendorResult.Error<bool>(VendorErrors.PlatformShop);

        var formerMembers = await vendorStore.DeleteAsync(id, clock.UtcNow, cancellationToken);
        if (formerMembers is null) return VendorResult.Error<bool>(VendorErrors.NotFound);

        foreach (var memberId in formerMembers)
        {
            await auditLog.WriteAsync("vendor.member_removed", actorCustomerId, memberId, "Vendor", id,
                details: new { vendorId = id, customerId = memberId, actor = actorCustomerId, self = false, byAdmin = true },
                cancellationToken: cancellationToken);
        }

        return VendorResult.Success(true);
    }

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

    public Task<bool> DeleteNoteAsync(int vendorId, int noteId, CancellationToken cancellationToken) =>
        vendorStore.DeleteNoteAsync(vendorId, noteId, cancellationToken);

    private static Dictionary<string, string[]> Validate(string name, string email)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Vendor name is required."];
        else if (name.Trim().Length > VendorValidation.MaxNameLength) errors["name"] = [$"Vendor name cannot exceed {VendorValidation.MaxNameLength} characters."];
        if (string.IsNullOrWhiteSpace(email)) errors["email"] = ["Vendor email is required."];
        else if (email.Trim().Length > VendorValidation.MaxEmailLength) errors["email"] = [$"Vendor email cannot exceed {VendorValidation.MaxEmailLength} characters."];
        return errors;
    }
}
