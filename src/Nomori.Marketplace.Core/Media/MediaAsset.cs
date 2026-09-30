namespace Nomori.Marketplace.Core.Media;

public enum MediaPurpose
{
    Category = 0,
    Manufacturer = 1,
    VendorLogo = 2,
    Product = 3
}

public enum MediaVisibility
{
    Public = 0
}

public sealed class MediaAsset
{
    public int Id { get; set; }
    public MediaPurpose Purpose { get; set; }
    public MediaVisibility Visibility { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public int SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int UploadedByCustomerId { get; set; }
    public int? VendorId { get; set; }
    public DateTime CreatedOnUtc { get; set; }
}

public sealed record MediaContent(byte[] Data, string MimeType, string Sha256);

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    public int MaxUploadBytes { get; init; } = 5 * 1024 * 1024;
}

/// <summary>Business-rule codes returned in <c>ProblemDetails.detail</c>. NotFound and Forbidden map to 404 and 403; others to 409.</summary>
public static class MediaErrors
{
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string InUse = "media.in_use";
}

/// <summary>Permissions and shop membership of the caller, resolved once per request from the session.</summary>
public sealed record MediaCaller(int CustomerId, bool CanManageCatalog, bool CanManageVendors, int? MemberVendorId);

public sealed record UploadMediaCommand(MediaPurpose? Purpose, int? VendorId, byte[] Data);

/// <summary>Outcome of a media operation: field errors (400), an error code (403, 404, 409), or a value.</summary>
public sealed record MediaResult<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors, string? ErrorCode = null)
{
    public bool Succeeded => Errors.Count == 0 && ErrorCode is null;
}

public static class MediaResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    public static MediaResult<T> Success<T>(T value) => new(value, NoErrors);

    public static MediaResult<T> Failure<T>(string field, string message) =>
        new(default, new Dictionary<string, string[]> { [field] = [message] });

    public static MediaResult<T> Error<T>(string errorCode) => new(default, NoErrors, errorCode);
}

public interface IMediaStore
{
    Task<int> InsertAsync(MediaAsset asset, byte[] data, CancellationToken cancellationToken);
    Task<MediaAsset?> GetAsync(int id, CancellationToken cancellationToken);
    Task<MediaContent?> GetContentAsync(int id, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);

    /// <summary>True when a category, manufacturer or vendor still points at the asset.</summary>
    Task<bool> IsReferencedAsync(int id, CancellationToken cancellationToken);
}

public interface IMediaService
{
    Task<MediaResult<MediaAsset>> UploadAsync(UploadMediaCommand command, MediaCaller caller, CancellationToken cancellationToken);
    Task<MediaContent?> GetContentAsync(int id, CancellationToken cancellationToken);
    Task<MediaResult<bool>> DeleteAsync(int id, MediaCaller caller, CancellationToken cancellationToken);
}
