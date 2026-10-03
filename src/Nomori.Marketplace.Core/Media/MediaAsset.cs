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

/// <summary>Where the bytes of an asset live. Metadata always stays in SQL Server.</summary>
public enum MediaStorageProvider
{
    Database = 0,
    ObjectStorage = 1
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
    public MediaStorageProvider StorageProvider { get; set; }

    /// <summary>Object key in the bucket when <see cref="StorageProvider"/> is ObjectStorage; otherwise null.</summary>
    public string? StorageKey { get; set; }
}

public sealed record MediaContent(byte[] Data, string MimeType, string Sha256);

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    public int MaxUploadBytes { get; init; } = 5 * 1024 * 1024;
}

/// <summary>Configuration section Media:Storage. Provider "Database" keeps bytes in SQL Server; "S3" uses an S3-compatible store such as MinIO.</summary>
public sealed class MediaStorageOptions
{
    public const string SectionName = "Media:Storage";

    public string Provider { get; init; } = "Database";

    public MediaS3Options S3 { get; init; } = new();

    public bool UsesObjectStorage => string.Equals(Provider, "S3", StringComparison.OrdinalIgnoreCase);
}

public sealed class MediaS3Options
{
    /// <summary>Address the API uses, for example http://localhost:9000 or http://minio:9000 inside Docker.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Address browsers use for presigned uploads and public images. Defaults to <see cref="Endpoint"/>.</summary>
    public string? PublicEndpoint { get; init; }

    public string Bucket { get; init; } = "nomori-media";
    public string Region { get; init; } = "us-east-1";
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>How long a presigned upload stays valid.</summary>
    public int UploadUrlLifetimeMinutes { get; init; } = 10;

    /// <summary>Cache lifetime of the redirect from /api/v1/media/{id} to the public object URL.</summary>
    public int RedirectCacheSeconds { get; init; } = 86400;
}

/// <summary>Business-rule codes returned in <c>ProblemDetails.detail</c>. NotFound and Forbidden map to 404 and 403; others to 409.</summary>
public static class MediaErrors
{
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string InUse = "media.in_use";
    public const string DirectUploadUnavailable = "media.direct_upload_unavailable";
    public const string UploadExpired = "media.upload_expired";
}

/// <summary>Permissions and shop membership of the caller, resolved once per request from the session.</summary>
public sealed record MediaCaller(int CustomerId, bool CanManageCatalog, bool CanManageVendors, int? MemberVendorId);

public sealed record UploadMediaCommand(MediaPurpose? Purpose, int? VendorId, byte[] Data);

/// <summary>Step 1 of a direct upload: who uploads what, so the API can authorize before handing out a presigned POST.</summary>
public sealed record CreateMediaUploadCommand(MediaPurpose? Purpose, int? VendorId, long SizeBytes);

/// <summary>A presigned POST the browser sends the file to, plus the id to complete the upload with.</summary>
public sealed record MediaUploadTicket(Guid UploadId, string Url, IReadOnlyDictionary<string, string> Fields, DateTime ExpiresOnUtc, int MaxBytes);

/// <summary>A started direct upload. The bytes sit under <see cref="ObjectKey"/> until the upload is completed and validated.</summary>
public sealed class MediaUpload
{
    public Guid Id { get; set; }
    public MediaPurpose Purpose { get; set; }
    public int? VendorId { get; set; }
    public int CustomerId { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public DateTime ExpiresOnUtc { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public int? MediaAssetId { get; set; }
}

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
    /// <summary>Saves the metadata. <paramref name="data"/> is stored in SQL Server for Database assets and must be null for ObjectStorage assets.</summary>
    Task<int> InsertAsync(MediaAsset asset, byte[]? data, CancellationToken cancellationToken);
    Task<MediaAsset?> GetAsync(int id, CancellationToken cancellationToken);
    Task<MediaContent?> GetContentAsync(int id, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);

    /// <summary>True when a category, manufacturer or vendor still points at the asset.</summary>
    Task<bool> IsReferencedAsync(int id, CancellationToken cancellationToken);
}

public interface IMediaUploadStore
{
    Task InsertAsync(MediaUpload upload, CancellationToken cancellationToken);
    Task<MediaUpload?> GetAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>Links the upload to its asset. False when another request completed it first.</summary>
    Task<bool> MarkCompletedAsync(Guid id, int mediaAssetId, CancellationToken cancellationToken);
}

/// <summary>S3-compatible object storage (MinIO in development). Only used when Media:Storage:Provider is S3.</summary>
/// <summary>An object read back from storage. <see cref="Data"/> is null when the object is larger than the limit asked for.</summary>
public sealed record StoredObject(long SizeBytes, byte[]? Data);

public interface IMediaObjectStorage
{
    /// <summary>False when Media:Storage:Provider is Database; the other members then throw.</summary>
    bool IsEnabled { get; }

    /// <summary>Presigned POST for exactly <paramref name="objectKey"/>, limited to 1..<paramref name="maxBytes"/> bytes.</summary>
    Task<(string Url, IReadOnlyDictionary<string, string> Fields)> CreatePresignedPostAsync(string objectKey, long maxBytes, DateTime expiresOnUtc);

    /// <summary>Null when the object does not exist. Objects above <paramref name="maxBytes"/> are not downloaded.</summary>
    Task<StoredObject?> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken);

    Task PutAsync(string objectKey, byte[] data, string contentType, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Browser-facing URL of a public object.</summary>
    string GetPublicUrl(string objectKey);
}

public interface IMediaService
{
    Task<MediaResult<MediaAsset>> UploadAsync(UploadMediaCommand command, MediaCaller caller, CancellationToken cancellationToken);
    Task<MediaResult<MediaUploadTicket>> CreateUploadAsync(CreateMediaUploadCommand command, MediaCaller caller, CancellationToken cancellationToken);
    Task<MediaResult<MediaAsset>> CompleteUploadAsync(Guid uploadId, MediaCaller caller, CancellationToken cancellationToken);
    Task<MediaAsset?> GetAsync(int id, CancellationToken cancellationToken);
    Task<MediaContent?> GetContentAsync(int id, CancellationToken cancellationToken);
    /// <summary>Public object URL for assets in object storage; null for assets served from the database.</summary>
    string? GetPublicUrl(MediaAsset asset);
    Task<MediaResult<bool>> DeleteAsync(int id, MediaCaller caller, CancellationToken cancellationToken);
}
