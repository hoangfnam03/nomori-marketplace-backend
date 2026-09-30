using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Media;

public sealed class MediaService(
    IMediaStore mediaStore,
    IVendorStore vendorStore,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<MediaOptions> options) : IMediaService
{
    public async Task<MediaResult<MediaAsset>> UploadAsync(
        UploadMediaCommand command, MediaCaller caller, CancellationToken cancellationToken)
    {
        if (command.Purpose is not { } purpose || !Enum.IsDefined(purpose))
            return MediaResult.Failure<MediaAsset>("purpose", "Purpose must be category, manufacturer, vendorLogo or product.");

        // Authorization first, so a caller who may not upload learns nothing about the file rules.
        var (vendorId, failure) = await ResolveOwnerAsync(purpose, command.VendorId, caller, cancellationToken);
        if (failure is not null) return failure;

        var data = command.Data;
        if (data.Length == 0) return MediaResult.Failure<MediaAsset>("file", "The file is empty.");
        if (data.Length > options.Value.MaxUploadBytes)
            return MediaResult.Failure<MediaAsset>("file", $"The file is larger than {options.Value.MaxUploadBytes / (1024 * 1024)} MB.");

        var mimeType = ImageSignature.Detect(data);
        if (mimeType is null)
            return MediaResult.Failure<MediaAsset>("file", "Only JPEG, PNG, GIF and WebP images are accepted.");

        var asset = new MediaAsset
        {
            Purpose = purpose,
            Visibility = MediaVisibility.Public,
            MimeType = mimeType,
            SizeBytes = data.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            UploadedByCustomerId = caller.CustomerId,
            VendorId = vendorId,
            CreatedOnUtc = clock.UtcNow
        };
        asset.Id = await mediaStore.InsertAsync(asset, data, cancellationToken);

        await auditLog.WriteAsync("media.uploaded", caller.CustomerId, entityType: "MediaAsset", entityId: asset.Id,
            details: new { mediaId = asset.Id, purpose = purpose.ToString(), vendorId, sizeBytes = asset.SizeBytes },
            cancellationToken: cancellationToken);
        return MediaResult.Success(asset);
    }

    public Task<MediaContent?> GetContentAsync(int id, CancellationToken cancellationToken) =>
        mediaStore.GetContentAsync(id, cancellationToken);

    public async Task<MediaResult<bool>> DeleteAsync(int id, MediaCaller caller, CancellationToken cancellationToken)
    {
        var asset = await mediaStore.GetAsync(id, cancellationToken);
        // Callers who could not manage the asset are told it does not exist.
        if (asset is null || !CanManage(asset, caller)) return MediaResult.Error<bool>(MediaErrors.NotFound);

        if (await mediaStore.IsReferencedAsync(id, cancellationToken))
            return MediaResult.Error<bool>(MediaErrors.InUse);

        if (!await mediaStore.DeleteAsync(id, cancellationToken)) return MediaResult.Error<bool>(MediaErrors.NotFound);

        await auditLog.WriteAsync("media.deleted", caller.CustomerId, entityType: "MediaAsset", entityId: id,
            details: new { mediaId = id, purpose = asset.Purpose.ToString(), vendorId = asset.VendorId },
            cancellationToken: cancellationToken);
        return MediaResult.Success(true);
    }

    private async Task<(int? VendorId, MediaResult<MediaAsset>? Failure)> ResolveOwnerAsync(
        MediaPurpose purpose, int? requestedVendorId, MediaCaller caller, CancellationToken cancellationToken)
    {
        switch (purpose)
        {
            case MediaPurpose.Category:
            case MediaPurpose.Manufacturer:
                if (!caller.CanManageCatalog) return (null, MediaResult.Error<MediaAsset>(MediaErrors.Forbidden));
                if (requestedVendorId is not null) return (null, MediaResult.Failure<MediaAsset>("vendorId", "Vendor id is not used for this purpose."));
                return (null, null);

            case MediaPurpose.Product:
                // Only shop members upload product images, and only for their own shop.
                if (caller.MemberVendorId is not { } productVendor
                    || (requestedVendorId is not null && requestedVendorId != productVendor))
                    return (null, MediaResult.Error<MediaAsset>(MediaErrors.Forbidden));
                return (productVendor, null);

            default:
                if (requestedVendorId is { } requested)
                {
                    if (requested != caller.MemberVendorId && !caller.CanManageVendors)
                        return (null, MediaResult.Error<MediaAsset>(MediaErrors.Forbidden));
                    if (await vendorStore.GetAsync(requested, cancellationToken) is null)
                        return (null, MediaResult.Failure<MediaAsset>("vendorId", "Vendor not found."));
                    return (requested, null);
                }

                if (caller.MemberVendorId is { } own) return (own, null);
                return caller.CanManageVendors
                    ? (null, MediaResult.Failure<MediaAsset>("vendorId", "Vendor id is required for a vendor logo."))
                    : (null, MediaResult.Error<MediaAsset>(MediaErrors.Forbidden));
        }
    }

    private static bool CanManage(MediaAsset asset, MediaCaller caller)
    {
        if (asset.UploadedByCustomerId == caller.CustomerId) return true;
        if (asset.VendorId is not null && asset.VendorId == caller.MemberVendorId) return true;
        return asset.Purpose is MediaPurpose.Category or MediaPurpose.Manufacturer
            ? caller.CanManageCatalog
            : caller.CanManageVendors;
    }
}
