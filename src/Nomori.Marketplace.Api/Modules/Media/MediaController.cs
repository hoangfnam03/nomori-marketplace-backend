using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Media;

/// <summary>Image upload and delivery (F08-A). Uploads are authorized per purpose; delivery is public.</summary>
[ApiController]
[Route("api/v1/media")]
public sealed class MediaController(
    IMediaService mediaService,
    IVendorAccessContext accessContext,
    IPermissionService permissionService,
    IOptions<MediaOptions> options,
    IOptions<MediaStorageOptions> storageOptions) : ControllerBase
{
    // Hard transport cap, above the configurable limit (Media:MaxUploadBytes, at most 20 MiB) plus multipart overhead.
    private const long MaxRequestBytes = 21L * 1024 * 1024;

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Upload([FromForm] UploadMediaRequest request, CancellationToken cancellationToken)
    {
        var caller = await BuildCallerAsync(cancellationToken);
        if (caller is null) return Unauthorized();

        var file = request.File;
        if (file is not null && file.Length > options.Value.MaxUploadBytes)
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["file"] = [$"The file is larger than {options.Value.MaxUploadBytes / (1024 * 1024)} MB."]
            }));

        byte[] data = [];
        if (file is not null)
        {
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, cancellationToken);
            data = buffer.ToArray();
        }

        var result = await mediaService.UploadAsync(new UploadMediaCommand(ParsePurpose(request.Purpose), request.VendorId, data), caller, cancellationToken);
        if (!result.Succeeded) return Failure(result);

        var response = MediaResponse.From(result.Value!);
        return Created(response.Url, response);
    }

    /// <summary>
    /// Step 1 of a direct upload: authorizes the caller and returns a presigned POST to object storage.
    /// 409 <c>media.direct_upload_unavailable</c> when the API stores images in the database; clients then use POST /api/v1/media.
    /// </summary>
    [HttpPost("uploads")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUpload([FromBody] CreateMediaUploadRequest request, CancellationToken cancellationToken)
    {
        var caller = await BuildCallerAsync(cancellationToken);
        if (caller is null) return Unauthorized();

        var result = await mediaService.CreateUploadAsync(
            new CreateMediaUploadCommand(ParsePurpose(request.Purpose), request.VendorId, request.SizeBytes ?? 0), caller, cancellationToken);
        if (!result.Succeeded) return Failure(result);

        var ticket = result.Value!;
        return Ok(new MediaUploadResponse(ticket.UploadId, ticket.Url, ticket.Fields, ticket.ExpiresOnUtc, ticket.MaxBytes));
    }

    /// <summary>Step 3 of a direct upload, after the browser posted the file: validates the bytes and creates the asset.</summary>
    [HttpPost("uploads/{id:guid}/complete")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteUpload(Guid id, CancellationToken cancellationToken)
    {
        var caller = await BuildCallerAsync(cancellationToken);
        if (caller is null) return Unauthorized();

        var result = await mediaService.CompleteUploadAsync(id, caller, cancellationToken);
        if (!result.Succeeded) return Failure(result);

        var response = MediaResponse.From(result.Value!);
        return Created(response.Url, response);
    }

    /// <summary>Images in object storage redirect to their public URL; images in the database are served here.</summary>
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var asset = await mediaService.GetAsync(id, cancellationToken);
        if (asset is null || asset.Visibility != MediaVisibility.Public) return NotFound();

        if (asset.StorageProvider == MediaStorageProvider.ObjectStorage)
        {
            var publicUrl = mediaService.GetPublicUrl(asset);
            if (publicUrl is null) return NotFound();

            // The id never points at other bytes, but the bucket address may change, so the redirect is cached for a day, not forever.
            Response.Headers.CacheControl = $"public, max-age={storageOptions.Value.S3.RedirectCacheSeconds}";
            return Redirect(publicUrl);
        }

        var content = await mediaService.GetContentAsync(id, cancellationToken);
        if (content is null) return NotFound();

        var etag = $"\"{content.Sha256}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        if (Request.Headers.IfNoneMatch == etag) return StatusCode(StatusCodes.Status304NotModified);

        return File(content.Data, content.MimeType);
    }

    [HttpDelete("{id:int}")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var caller = await BuildCallerAsync(cancellationToken);
        if (caller is null) return Unauthorized();

        var result = await mediaService.DeleteAsync(id, caller, cancellationToken);
        return result.Succeeded ? NoContent() : Failure(result);
    }

    private static MediaPurpose? ParsePurpose(string? value) =>
        Enum.TryParse<MediaPurpose>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private async Task<MediaCaller?> BuildCallerAsync(CancellationToken cancellationToken)
    {
        var vendorCaller = await accessContext.GetCallerAsync(cancellationToken);
        if (vendorCaller.CustomerId is not { } customerId) return null;

        var canManageCatalog = await permissionService.HasPermissionAsync(customerId, PermissionCodes.CatalogManage, cancellationToken);
        return new MediaCaller(customerId, canManageCatalog, vendorCaller.IsAdmin, vendorCaller.MemberVendorId);
    }

    private IActionResult Failure<T>(MediaResult<T> result)
    {
        if (result.Errors.Count > 0)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));

        return result.ErrorCode switch
        {
            MediaErrors.NotFound => NotFound(),
            MediaErrors.Forbidden => StatusCode(StatusCodes.Status403Forbidden),
            _ => Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Media operation failed", Detail = result.ErrorCode })
        };
    }
}

public sealed class UploadMediaRequest
{
    public IFormFile? File { get; init; }

    public string? Purpose { get; init; }

    public int? VendorId { get; init; }
}

public sealed class CreateMediaUploadRequest
{
    public string? Purpose { get; init; }

    public int? VendorId { get; init; }

    /// <summary>Exact size of the file. The presigned policy admits no larger body.</summary>
    public long? SizeBytes { get; init; }
}

/// <summary>Send the file as multipart/form-data to <c>Url</c>: every entry of <c>Fields</c> first, then the file as <c>file</c>.</summary>
public sealed record MediaUploadResponse(
    Guid UploadId, string Url, IReadOnlyDictionary<string, string> Fields, DateTime ExpiresOnUtc, int MaxBytes);

public sealed record MediaResponse(
    int Id, string Purpose, string MimeType, int SizeBytes, int? VendorId, DateTime CreatedOnUtc, string Url)
{
    public static MediaResponse From(MediaAsset a) => new(
        a.Id, char.ToLowerInvariant(a.Purpose.ToString()[0]) + a.Purpose.ToString()[1..], a.MimeType, a.SizeBytes,
        a.VendorId, a.CreatedOnUtc, $"/api/v1/media/{a.Id}");
}
