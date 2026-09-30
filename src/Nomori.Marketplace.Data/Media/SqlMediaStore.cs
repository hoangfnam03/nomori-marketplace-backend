using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Media;

namespace Nomori.Marketplace.Data.Media;

public sealed class SqlMediaStore(IOptions<DatabaseOptions> options) : IMediaStore
{
    private const string SelectColumns =
        "Id, Purpose, Visibility, MimeType, SizeBytes, Sha256, UploadedByCustomerId, VendorId, CreatedOnUtc";

    public async Task<int> InsertAsync(MediaAsset asset, byte[] data, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var assetCmd = connection.CreateCommand();
        assetCmd.Transaction = transaction;
        assetCmd.CommandText = """
            INSERT INTO MediaAsset (Purpose, Visibility, MimeType, SizeBytes, Sha256, UploadedByCustomerId, VendorId, CreatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Purpose, @Visibility, @MimeType, @SizeBytes, @Sha256, @UploadedBy, @VendorId, @CreatedOnUtc)
            """;
        assetCmd.Parameters.AddWithValue("@Purpose", (int)asset.Purpose);
        assetCmd.Parameters.AddWithValue("@Visibility", (int)asset.Visibility);
        assetCmd.Parameters.AddWithValue("@MimeType", asset.MimeType);
        assetCmd.Parameters.AddWithValue("@SizeBytes", asset.SizeBytes);
        assetCmd.Parameters.AddWithValue("@Sha256", asset.Sha256);
        assetCmd.Parameters.AddWithValue("@UploadedBy", asset.UploadedByCustomerId);
        assetCmd.Parameters.AddWithValue("@VendorId", (object?)asset.VendorId ?? DBNull.Value);
        assetCmd.Parameters.AddWithValue("@CreatedOnUtc", asset.CreatedOnUtc);
        var id = (int)(await assetCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var binaryCmd = connection.CreateCommand();
        binaryCmd.Transaction = transaction;
        binaryCmd.CommandText = "INSERT INTO MediaAssetBinary (MediaAssetId, Data) VALUES (@Id, @Data)";
        binaryCmd.Parameters.AddWithValue("@Id", id);
        binaryCmd.Parameters.Add("@Data", System.Data.SqlDbType.VarBinary, -1).Value = data;
        await binaryCmd.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<MediaAsset?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM MediaAsset WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<MediaContent?> GetContentAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.Data, a.MimeType, a.Sha256
            FROM MediaAsset a INNER JOIN MediaAssetBinary b ON b.MediaAssetId = a.Id
            WHERE a.Id = @Id AND a.Visibility = 0
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MediaContent((byte[])reader[0], reader.GetString(1), reader.GetString(2).Trim())
            : null;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM MediaAsset WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> IsReferencedAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM Category WHERE PictureId = @Id)
                          OR EXISTS (SELECT 1 FROM Manufacturer WHERE PictureId = @Id)
                          OR EXISTS (SELECT 1 FROM Vendor WHERE PictureId = @Id AND Deleted = 0)
                        THEN 1 ELSE 0 END
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static MediaAsset Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Purpose = (MediaPurpose)r.GetInt32(1),
        Visibility = (MediaVisibility)r.GetInt32(2),
        MimeType = r.GetString(3),
        SizeBytes = r.GetInt32(4),
        Sha256 = r.GetString(5).Trim(),
        UploadedByCustomerId = r.GetInt32(6),
        VendorId = r.IsDBNull(7) ? null : r.GetInt32(7),
        CreatedOnUtc = r.GetDateTime(8)
    };
}
