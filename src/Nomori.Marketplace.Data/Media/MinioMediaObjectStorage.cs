using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Nomori.Marketplace.Core.Media;

namespace Nomori.Marketplace.Data.Media;

/// <summary>
/// S3-compatible storage through the MinIO SDK. Server-side calls go to <see cref="MediaS3Options.Endpoint"/>; presigned
/// posts are signed for <see cref="MediaS3Options.PublicEndpoint"/>, because the signature covers the host the browser calls.
/// </summary>
public sealed class MinioMediaObjectStorage : IMediaObjectStorage, IDisposable
{
    private readonly MediaS3Options _options;
    private readonly IMinioClient _client;
    private readonly IMinioClient _signingClient;
    private readonly string _publicBase;

    public MinioMediaObjectStorage(IOptions<MediaStorageOptions> options)
    {
        _options = options.Value.S3;
        var endpoint = new Uri(_options.Endpoint);
        var publicEndpoint = new Uri(string.IsNullOrWhiteSpace(_options.PublicEndpoint) ? _options.Endpoint : _options.PublicEndpoint);
        _client = Build(endpoint);
        // With the region set the SDK signs locally instead of asking the server for the bucket region.
        _signingClient = Build(publicEndpoint);
        _publicBase = $"{publicEndpoint.GetLeftPart(UriPartial.Authority)}/{_options.Bucket}";
    }

    public bool IsEnabled => true;

    public async Task<(string Url, IReadOnlyDictionary<string, string> Fields)> CreatePresignedPostAsync(
        string objectKey, long maxBytes, DateTime expiresOnUtc)
    {
        var policy = new PostPolicy();
        policy.SetBucket(_options.Bucket);
        policy.SetKey(objectKey);
        policy.SetContentRange(1, maxBytes);
        policy.SetExpires(expiresOnUtc);

        var (uri, fields) = await _signingClient.PresignedPostPolicyAsync(policy);
        return (uri.ToString(), new Dictionary<string, string>(fields));
    }

    public async Task<StoredObject?> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken)
    {
        ObjectStat stat;
        try
        {
            stat = await _client.StatObjectAsync(
                new StatObjectArgs().WithBucket(_options.Bucket).WithObject(objectKey), cancellationToken);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }

        if (stat.Size > maxBytes) return new StoredObject(stat.Size, null);

        using var buffer = new MemoryStream((int)stat.Size);
        await _client.GetObjectAsync(
            new GetObjectArgs().WithBucket(_options.Bucket).WithObject(objectKey)
                .WithCallbackStream((stream, ct) => stream.CopyToAsync(buffer, ct)),
            cancellationToken);
        return new StoredObject(stat.Size, buffer.ToArray());
    }

    public async Task PutAsync(string objectKey, byte[] data, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(data, writable: false);
        await _client.PutObjectAsync(
            new PutObjectArgs().WithBucket(_options.Bucket).WithObject(objectKey)
                .WithStreamData(stream).WithObjectSize(data.Length).WithContentType(contentType)
                .WithHeaders(new Dictionary<string, string>
                {
                    // Keys are never reused, so browsers may keep the bytes forever.
                    ["Cache-Control"] = "public, max-age=31536000, immutable"
                }),
            cancellationToken);
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        _client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_options.Bucket).WithObject(objectKey), cancellationToken);

    public string GetPublicUrl(string objectKey) => $"{_publicBase}/{objectKey}";

    public void Dispose()
    {
        _client.Dispose();
        _signingClient.Dispose();
    }

    private IMinioClient Build(Uri endpoint) => new MinioClient()
        .WithEndpoint(endpoint.Authority)
        .WithSSL(endpoint.Scheme == Uri.UriSchemeHttps)
        .WithRegion(_options.Region)
        .WithCredentials(_options.AccessKey, _options.SecretKey)
        .Build();
}

/// <summary>Used when Media:Storage:Provider is Database: direct uploads are unavailable and bytes stay in SQL Server.</summary>
public sealed class DisabledMediaObjectStorage : IMediaObjectStorage
{
    public bool IsEnabled => false;

    public Task<(string Url, IReadOnlyDictionary<string, string> Fields)> CreatePresignedPostAsync(string objectKey, long maxBytes, DateTime expiresOnUtc) =>
        throw Disabled();

    public Task<StoredObject?> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken) => throw Disabled();

    public Task PutAsync(string objectKey, byte[] data, string contentType, CancellationToken cancellationToken) => throw Disabled();

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => throw Disabled();

    public string GetPublicUrl(string objectKey) => throw Disabled();

    private static InvalidOperationException Disabled() => new("Object storage is not configured (Media:Storage:Provider is Database).");
}
