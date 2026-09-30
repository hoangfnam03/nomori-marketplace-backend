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
    IOptions<MediaOptions> options) : ControllerBase
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

        MediaPurpose? purpose = null;
        if (Enum.TryParse<MediaPurpose>(request.Purpose, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            purpose = parsed;

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

        var result = await mediaService.UploadAsync(new UploadMediaCommand(purpose, request.VendorId, data), caller, cancellationToken);
        if (!result.Succeeded) return Failure(result);

        var response = MediaResponse.From(result.Value!);
        return Created(response.Url, response);
    }

    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
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

public sealed record MediaResponse(
    int Id, string Purpose, string MimeType, int SizeBytes, int? VendorId, DateTime CreatedOnUtc, string Url)
{
    public static MediaResponse From(MediaAsset a) => new(
        a.Id, char.ToLowerInvariant(a.Purpose.ToString()[0]) + a.Purpose.ToString()[1..], a.MimeType, a.SizeBytes,
        a.VendorId, a.CreatedOnUtc, $"/api/v1/media/{a.Id}");
}
