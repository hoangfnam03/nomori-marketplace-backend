using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Seo;

namespace Nomori.Marketplace.Data.Seo;

/// <summary>The public rows behind the sitemap. The visibility rule is the one of the catalog: live product, shop active, window open.</summary>
public sealed class SqlSeoStore(IOptions<DatabaseOptions> options) : ISeoStore
{
    public async Task<IReadOnlyList<SitemapEntry>> ListProductsAsync(int afterId, int take, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@Take) p.Id, p.Name, p.UpdatedOnUtc
            FROM Product p INNER JOIN Vendor v ON v.Id = p.VendorId
            WHERE p.Id > @AfterId AND p.Deleted = 0 AND p.Status = 1 AND v.Active = 1 AND v.Deleted = 0
              AND (p.AvailableStartUtc IS NULL OR p.AvailableStartUtc <= @Now) AND (p.AvailableEndUtc IS NULL OR @Now < p.AvailableEndUtc)
            ORDER BY p.Id
            """;
        cmd.Parameters.AddWithValue("@Take", take);
        cmd.Parameters.AddWithValue("@AfterId", afterId);
        cmd.Parameters.Add(new SqlParameter("@Now", SqlDbType.DateTime2) { Value = nowUtc });
        return await ReadAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<SitemapEntry>> ListShopsAsync(int afterId, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TOP (@Take) Id, Name, UpdatedOnUtc FROM Vendor WHERE Id > @AfterId AND Active = 1 AND Deleted = 0 ORDER BY Id";
        cmd.Parameters.AddWithValue("@Take", take);
        cmd.Parameters.AddWithValue("@AfterId", afterId);
        return await ReadAsync(cmd, cancellationToken);
    }

    private static async Task<IReadOnlyList<SitemapEntry>> ReadAsync(SqlCommand cmd, CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SitemapEntry>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new SitemapEntry(reader.GetInt32(0), reader.GetString(1), DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)));
        return rows;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
