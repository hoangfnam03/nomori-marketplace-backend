using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Media;

public sealed partial class MediaService(
    IMediaStore mediaStore,
    IMediaUploadStore uploadStore,
    IMediaObjectStorage objectStorage,
    IVendorStore vendorStore,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<MediaOptions> options,
    IOptions<MediaStorageOptions> storageOptions,
    ILogger<MediaService> logger) : IMediaService
{
    // A browser may start the POST just before the policy expires; completion stays open a little longer.
    private static readonly TimeSpan CompletionGrace = TimeSpan.FromMinutes(30);

    private int MaxBytes => options.Value.MaxUploadBytes;

    public async Task<MediaResult<MediaAsset>> UploadAsync(
        UploadMediaCommand command, MediaCaller caller, CancellationToken cancellationToken)
    {
        if (command.Purpose is not { } purpose || !Enum.IsDefined(purpose))
            return MediaResult.Failure<MediaAsset>("purpose", PurposeMessage);

        // Authorization first, so a caller who may not upload learns nothing about the file rules.
        var (vendorId, failure) = await ResolveOwnerAsync<MediaAsset>(purpose, command.VendorId, caller, cancellationToken);
        if (failure is not null) return failure;

        var data = command.Data;
        var (mimeType, invalid) = Validate<MediaAsset>(data);
        if (invalid is not null) return invalid;

        var asset = NewAsset(purpose, vendorId, mimeType!, data, caller);
        if (objectStorage.IsEnabled)
        {
            asset.StorageProvider = MediaStorageProvider.ObjectStorage;
            asset.StorageKey = PublicKey(purpose, mimeType!);
            await objectStorage.PutAsync(asset.StorageKey, data, mimeType!, cancellationToken);
            asset.Id = await InsertOrRemoveObjectAsync(asset, cancellationToken);
        }
        else
        {
            asset.Id = await mediaStore.InsertAsync(asset, data, cancellationToken);
        }

        await AuditUploadAsync(asset, direct: false, cancellationToken);
        return MediaResult.Success(asset);
    }

    public async Task<MediaResult<MediaUploadTicket>> CreateUploadAsync(
        CreateMediaUploadCommand command, MediaCaller caller, CancellationToken cancellationToken)
    {
        if (!objectStorage.IsEnabled) return MediaResult.Error<MediaUploadTicket>(MediaErrors.DirectUploadUnavailable);

        if (command.Purpose is not { } purpose || !Enum.IsDefined(purpose))
            return MediaResult.Failure<MediaUploadTicket>("purpose", PurposeMessage);

        var (vendorId, failure) = await ResolveOwnerAsync<MediaUploadTicket>(purpose, command.VendorId, caller, cancellationToken);
        if (failure is not null) return failure;

        if (command.SizeBytes <= 0) return MediaResult.Failure<MediaUploadTicket>("file", EmptyMessage);
        if (command.SizeBytes > MaxBytes) return MediaResult.Failure<MediaUploadTicket>("file", TooLargeMessage);

        var now = clock.UtcNow;
        var upload = new MediaUpload
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            VendorId = vendorId,
            CustomerId = caller.CustomerId,
            CreatedOnUtc = now,
            ExpiresOnUtc = now.AddMinutes(storageOptions.Value.S3.UploadUrlLifetimeMinutes)
        };
        // The bucket expires everything under pending/ after a day, so abandoned uploads clean themselves up.
        upload.ObjectKey = $"pending/{upload.Id:N}";

        // The policy only admits the size the browser announced, so the stored file cannot grow past it.
        var maxBytes = (int)command.SizeBytes;
        var (url, fields) = await objectStorage.CreatePresignedPostAsync(upload.ObjectKey, maxBytes, upload.ExpiresOnUtc);
        await uploadStore.InsertAsync(upload, cancellationToken);

        return MediaResult.Success(new MediaUploadTicket(upload.Id, url, fields, upload.ExpiresOnUtc, maxBytes));
    }

    public async Task<MediaResult<MediaAsset>> CompleteUploadAsync(
        Guid uploadId, MediaCaller caller, CancellationToken cancellationToken)
    {
        if (!objectStorage.IsEnabled) return MediaResult.Error<MediaAsset>(MediaErrors.DirectUploadUnavailable);

        // Uploads belong to the account that started them; anyone else is told they do not exist.
        var upload = await uploadStore.GetAsync(uploadId, cancellationToken);
        if (upload is null || upload.CustomerId != caller.CustomerId) return MediaResult.Error<MediaAsset>(MediaErrors.NotFound);

        // Completing twice (a retried request) returns the same asset.
        if (upload.MediaAssetId is { } completedId) return await CompletedAsync(completedId, cancellationToken);

        if (clock.UtcNow > upload.ExpiresOnUtc + CompletionGrace) return MediaResult.Error<MediaAsset>(MediaErrors.UploadExpired);

        // The caller may have lost access since the upload started.
        var (vendorId, failure) = await ResolveOwnerAsync<MediaAsset>(upload.Purpose, upload.VendorId, caller, cancellationToken);
        if (failure is not null) return failure;

        var stored = await objectStorage.ReadAsync(upload.ObjectKey, MaxBytes, cancellationToken);
        if (stored is null) return MediaResult.Failure<MediaAsset>("file", "The file has not been uploaded.");

        var data = stored.Data ?? [];
        var (mimeType, invalid) = stored.Data is null
            ? (null, MediaResult.Failure<MediaAsset>("file", TooLargeMessage))
            : Validate<MediaAsset>(data);
        if (invalid is not null)
        {
            await TryDeleteObjectAsync(upload.ObjectKey);
            return invalid;
        }

        // Bytes are copied rather than moved: the stored copy gets the detected content type, not what the browser claimed.
        var asset = NewAsset(upload.Purpose, vendorId, mimeType!, data, caller);
        asset.StorageProvider = MediaStorageProvider.ObjectStorage;
        asset.StorageKey = PublicKey(upload.Purpose, mimeType!);
        await objectStorage.PutAsync(asset.StorageKey, data, mimeType!, cancellationToken);
        asset.Id = await InsertOrRemoveObjectAsync(asset, cancellationToken);

        if (!await uploadStore.MarkCompletedAsync(upload.Id, asset.Id, cancellationToken))
        {
            // A concurrent request completed the same upload first; keep its asset and drop this one.
            await mediaStore.DeleteAsync(asset.Id, cancellationToken);
            await TryDeleteObjectAsync(asset.StorageKey);
            var winner = await uploadStore.GetAsync(upload.Id, cancellationToken);
            return winner?.MediaAssetId is { } winnerId
                ? await CompletedAsync(winnerId, cancellationToken)
                : MediaResult.Error<MediaAsset>(MediaErrors.NotFound);
        }

        await TryDeleteObjectAsync(upload.ObjectKey);
        await AuditUploadAsync(asset, direct: true, cancellationToken);
        return MediaResult.Success(asset);
    }

    public Task<MediaAsset?> GetAsync(int id, CancellationToken cancellationToken) =>
        mediaStore.GetAsync(id, cancellationToken);

    public Task<MediaContent?> GetContentAsync(int id, CancellationToken cancellationToken) =>
        mediaStore.GetContentAsync(id, cancellationToken);

    public string? GetPublicUrl(MediaAsset asset) =>
        asset is { StorageProvider: MediaStorageProvider.ObjectStorage, StorageKey: { } key } && objectStorage.IsEnabled
            ? objectStorage.GetPublicUrl(key)
            : null;

    public async Task<MediaResult<bool>> DeleteAsync(int id, MediaCaller caller, CancellationToken cancellationToken)
    {
        var asset = await mediaStore.GetAsync(id, cancellationToken);
        // Callers who could not manage the asset are told it does not exist.
        if (asset is null || !CanManage(asset, caller)) return MediaResult.Error<bool>(MediaErrors.NotFound);

        if (await mediaStore.IsReferencedAsync(id, cancellationToken))
            return MediaResult.Error<bool>(MediaErrors.InUse);

        if (!await mediaStore.DeleteAsync(id, cancellationToken)) return MediaResult.Error<bool>(MediaErrors.NotFound);

        if (asset is { StorageProvider: MediaStorageProvider.ObjectStorage, StorageKey: { } key } && objectStorage.IsEnabled)
            await TryDeleteObjectAsync(key);

        await auditLog.WriteAsync("media.deleted", caller.CustomerId, entityType: "MediaAsset", entityId: id,
            details: new { mediaId = id, purpose = asset.Purpose.ToString(), vendorId = asset.VendorId },
            cancellationToken: cancellationToken);
        return MediaResult.Success(true);
    }

    private const string PurposeMessage = "Purpose must be category, manufacturer, vendorLogo or product.";
    private const string EmptyMessage = "The file is empty.";
    private string TooLargeMessage => $"The file is larger than {MaxBytes / (1024 * 1024)} MB.";

    private (string? MimeType, MediaResult<T>? Failure) Validate<T>(byte[] data)
    {
        if (data.Length == 0) return (null, MediaResult.Failure<T>("file", EmptyMessage));
        if (data.Length > MaxBytes) return (null, MediaResult.Failure<T>("file", TooLargeMessage));

        var mimeType = ImageSignature.Detect(data);
        return mimeType is null
            ? (null, MediaResult.Failure<T>("file", "Only JPEG, PNG, GIF and WebP images are accepted."))
            : (mimeType, null);
    }

    private MediaAsset NewAsset(MediaPurpose purpose, int? vendorId, string mimeType, byte[] data, MediaCaller caller) => new()
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

    /// <summary>public/{purpose}/{yyyy}/{MM}/{random}.{ext}. Keys are never reused, which keeps the long cache lifetime safe.</summary>
    private string PublicKey(MediaPurpose purpose, string mimeType)
    {
        var now = clock.UtcNow;
        var folder = char.ToLowerInvariant(purpose.ToString()[0]) + purpose.ToString()[1..];
        var extension = mimeType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/gif" => "gif",
            "image/webp" => "webp",
            _ => "bin"
        };
        return $"public/{folder}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}.{extension}";
    }

    private async Task<int> InsertOrRemoveObjectAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            return await mediaStore.InsertAsync(asset, null, cancellationToken);
        }
        catch
        {
            await TryDeleteObjectAsync(asset.StorageKey!);
            throw;
        }
    }

    private async Task<MediaResult<MediaAsset>> CompletedAsync(int assetId, CancellationToken cancellationToken)
    {
        var asset = await mediaStore.GetAsync(assetId, cancellationToken);
        return asset is null ? MediaResult.Error<MediaAsset>(MediaErrors.NotFound) : MediaResult.Success(asset);
    }

    // Cleanup must not fail the request: the database is already correct, and pending/ objects also expire on their own.
    private async Task TryDeleteObjectAsync(string key)
    {
        try
        {
            await objectStorage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogObjectDeleteFailed(ex, key);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete media object {ObjectKey}.")]
    private partial void LogObjectDeleteFailed(Exception exception, string objectKey);

    private Task AuditUploadAsync(MediaAsset asset, bool direct, CancellationToken cancellationToken) =>
        auditLog.WriteAsync("media.uploaded", asset.UploadedByCustomerId, entityType: "MediaAsset", entityId: asset.Id,
            details: new
            {
                mediaId = asset.Id, purpose = asset.Purpose.ToString(), vendorId = asset.VendorId, sizeBytes = asset.SizeBytes,
                storage = asset.StorageProvider.ToString(), direct
            },
            cancellationToken: cancellationToken);

    private async Task<(int? VendorId, MediaResult<T>? Failure)> ResolveOwnerAsync<T>(
        MediaPurpose purpose, int? requestedVendorId, MediaCaller caller, CancellationToken cancellationToken)
    {
        switch (purpose)
        {
            case MediaPurpose.Category:
            case MediaPurpose.Manufacturer:
                if (!caller.CanManageCatalog) return (null, MediaResult.Error<T>(MediaErrors.Forbidden));
                if (requestedVendorId is not null) return (null, MediaResult.Failure<T>("vendorId", "Vendor id is not used for this purpose."));
                return (null, null);

            case MediaPurpose.Product:
                // Only shop members upload product images, and only for their own shop.
                if (caller.MemberVendorId is not { } productVendor
                    || (requestedVendorId is not null && requestedVendorId != productVendor))
                    return (null, MediaResult.Error<T>(MediaErrors.Forbidden));
                return (productVendor, null);

            default:
                if (requestedVendorId is { } requested)
                {
                    if (requested != caller.MemberVendorId && !caller.CanManageVendors)
                        return (null, MediaResult.Error<T>(MediaErrors.Forbidden));
                    if (await vendorStore.GetAsync(requested, cancellationToken) is null)
                        return (null, MediaResult.Failure<T>("vendorId", "Vendor not found."));
                    return (requested, null);
                }

                if (caller.MemberVendorId is { } own) return (own, null);
                return caller.CanManageVendors
                    ? (null, MediaResult.Failure<T>("vendorId", "Vendor id is required for a vendor logo."))
                    : (null, MediaResult.Error<T>(MediaErrors.Forbidden));
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
