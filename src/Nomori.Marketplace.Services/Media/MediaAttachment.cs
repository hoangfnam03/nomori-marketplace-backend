using Nomori.Marketplace.Core.Media;

namespace Nomori.Marketplace.Services.Media;

/// <summary>
/// Checks that a <c>PictureId</c> on another record points at a real asset uploaded for that purpose.
/// An avatar must also have been uploaded by <paramref name="ownerCustomerId"/>, so nobody can use another customer's picture.
/// </summary>
public static class MediaAttachment
{
    public static async Task ValidateAsync(
        IMediaStore store, int pictureId, MediaPurpose purpose, int? vendorId,
        IDictionary<string, string[]> errors, CancellationToken cancellationToken, string field = "pictureId", int? ownerCustomerId = null)
    {
        if (pictureId == 0) return;

        var asset = pictureId < 0 ? null : await store.GetAsync(pictureId, cancellationToken);
        var valid = asset is not null
            && asset.Purpose == purpose
            && (purpose != MediaPurpose.VendorLogo || asset.VendorId == vendorId)
            && (purpose != MediaPurpose.CustomerAvatar || asset.UploadedByCustomerId == ownerCustomerId);
        if (!valid) errors[field] = ["Picture does not exist or cannot be used here."];
    }
}
