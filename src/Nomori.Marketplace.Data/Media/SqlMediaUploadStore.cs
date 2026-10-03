using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Media;

namespace Nomori.Marketplace.Data.Media;

public sealed class SqlMediaUploadStore(IOptions<DatabaseOptions> options) : IMediaUploadStore
{
    public async Task InsertAsync(MediaUpload upload, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MediaUpload (Id, Purpose, VendorId, CustomerId, ObjectKey, ExpiresOnUtc, CreatedOnUtc)
            VALUES (@Id, @Purpose, @VendorId, @CustomerId, @ObjectKey, @ExpiresOnUtc, @CreatedOnUtc)
            """;
        cmd.Parameters.AddWithValue("@Id", upload.Id);
        cmd.Parameters.AddWithValue("@Purpose", (int)upload.Purpose);
        cmd.Parameters.AddWithValue("@VendorId", (object?)upload.VendorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomerId", upload.CustomerId);
        cmd.Parameters.AddWithValue("@ObjectKey", upload.ObjectKey);
        cmd.Parameters.AddWithValue("@ExpiresOnUtc", upload.ExpiresOnUtc);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", upload.CreatedOnUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MediaUpload?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Purpose, VendorId, CustomerId, ObjectKey, ExpiresOnUtc, CreatedOnUtc, MediaAssetId
            FROM MediaUpload WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        return new MediaUpload
        {
            Id = r.GetGuid(0),
            Purpose = (MediaPurpose)r.GetInt32(1),
            VendorId = r.IsDBNull(2) ? null : r.GetInt32(2),
            CustomerId = r.GetInt32(3),
            ObjectKey = r.GetString(4),
            ExpiresOnUtc = r.GetDateTime(5),
            CreatedOnUtc = r.GetDateTime(6),
            MediaAssetId = r.IsDBNull(7) ? null : r.GetInt32(7)
        };
    }

    public async Task<bool> MarkCompletedAsync(Guid id, int mediaAssetId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE MediaUpload SET MediaAssetId = @AssetId WHERE Id = @Id AND MediaAssetId IS NULL";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@AssetId", mediaAssetId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
