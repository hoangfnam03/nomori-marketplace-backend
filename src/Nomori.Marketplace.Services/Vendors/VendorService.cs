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

    public async Task<VendorResult<Vendor>> UpdateAsync(UpdateVendorCommand command, VendorCaller caller, CancellationToken cancellationToken)
    {
        var existing = await vendorStore.GetAsync(command.Id, cancellationToken);
        // Members of other shops and customers are told the shop does not exist.
        if (existing is null || !(caller.IsAdmin || caller.IsMemberOf(existing.Id)))
            return VendorResult.Error<Vendor>(VendorErrors.NotFound);

        // Locking, ordering and internal notes stay with administrators.
        if (!caller.IsAdmin && (command.AdminComment is not null || command.Active is not null || command.DisplayOrder is not null))
            return VendorResult.Error<Vendor>(VendorErrors.Forbidden);
        if (!caller.IsAdmin && !existing.Active) return VendorResult.Error<Vendor>(VendorErrors.Inactive);

        var errors = new Dictionary<string, string[]>();
        var name = command.Name?.Trim() ?? string.Empty;
        var email = VendorValidation.NormalizeEmail(command.Email);
        var phone = VendorValidation.NullIfBlank(command.PhoneNumber);
        VendorValidation.ValidateShopName(name, errors, "name");
        VendorValidation.ValidateEmail(email, errors);
        // Shops created before phone numbers were kept have none; an administrator may leave it empty, a member may not.
        if (phone is not null || !caller.IsAdmin) VendorValidation.ValidatePhone(phone, errors);
        VendorValidation.ValidateMaxLength(command.TaxCode, VendorValidation.MaxTaxCodeLength, "taxCode", "Tax code", errors);
        VendorValidation.ValidateMaxLength(command.BusinessAddress, VendorValidation.MaxBusinessAddressLength, "businessAddress", "Business address", errors);
        if (existing.IsPlatformShop && command.Active == false)
            errors["active"] = ["The platform shop cannot be deactivated."];
        if (command.PictureId is { } pictureId && pictureId != existing.PictureId)
            await MediaAttachment.ValidateAsync(mediaStore, pictureId, MediaPurpose.VendorLogo, existing.Id, errors, cancellationToken);
        if (!errors.ContainsKey("name") && await vendorStore.NameExistsAsync(name, existing.Id, cancellationToken))
            errors["name"] = ["Another shop already uses this name."];
        if (errors.Count > 0) return VendorResult.Failure<Vendor>(errors);

        var before = Snapshot(existing);
        existing.Name = name;
        existing.Email = email;
        existing.PhoneNumber = phone;
        existing.Description = VendorValidation.NullIfBlank(command.Description);
        existing.TaxCode = VendorValidation.NullIfBlank(command.TaxCode);
        existing.BusinessAddress = VendorValidation.NullIfBlank(command.BusinessAddress);
        if (command.PictureId is { } newPicture) existing.PictureId = newPicture;
        if (command.AdminComment is not null) existing.AdminComment = VendorValidation.NullIfBlank(command.AdminComment);
        if (command.Active is { } active) existing.Active = active;
        if (command.DisplayOrder is { } displayOrder) existing.DisplayOrder = displayOrder;

        var changed = Snapshot(existing).Where(field => before[field.Key] != field.Value).Select(field => field.Key).ToList();
        if (changed.Count == 0) return VendorResult.Success(existing);

        existing.UpdatedOnUtc = clock.UtcNow;
        await vendorStore.UpdateAsync(existing, cancellationToken);
        // Field names only: the values may be personal data.
        await auditLog.WriteAsync("vendor.updated", caller.CustomerId, entityType: "Vendor", entityId: existing.Id,
            details: new { vendorId = existing.Id, fields = changed, byAdmin = caller.IsAdmin },
            cancellationToken: cancellationToken);
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

    private static Dictionary<string, string?> Snapshot(Vendor v) => new()
    {
        ["name"] = v.Name,
        ["email"] = v.Email,
        ["phoneNumber"] = v.PhoneNumber,
        ["description"] = v.Description,
        ["taxCode"] = v.TaxCode,
        ["businessAddress"] = v.BusinessAddress,
        ["pictureId"] = v.PictureId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["adminComment"] = v.AdminComment,
        ["active"] = v.Active ? "true" : "false",
        ["displayOrder"] = v.DisplayOrder.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
