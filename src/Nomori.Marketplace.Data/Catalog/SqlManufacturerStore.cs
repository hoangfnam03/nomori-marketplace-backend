using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

public sealed class SqlManufacturerStore(IOptions<DatabaseOptions> options) : IManufacturerStore
{
    private const string SelectColumns =
        "Id, Name, Description, PictureId, Published, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc";

    public async Task<Manufacturer?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Manufacturer WHERE Id = @Id AND Deleted = 0";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<(IReadOnlyList<Manufacturer> Items, int TotalCount)> GetPagedAsync(ManufacturerQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var where = BuildWhere(query);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM Manufacturer WHERE {where}";
        AddFilterParams(countCmd, query);
        var totalCount = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var cmd = connection.CreateCommand();
        var offset = (query.Page - 1) * query.PageSize;
        cmd.CommandText = $"SELECT {SelectColumns} FROM Manufacturer WHERE {where} ORDER BY DisplayOrder, Name OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
        AddFilterParams(cmd, query);
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@PageSize", query.PageSize);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Manufacturer>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return (items, totalCount);
    }

    public async Task<int> InsertAsync(Manufacturer manufacturer, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Manufacturer (Name, Description, PictureId, Published, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Name, @Description, @PictureId, @Published, 0, @DisplayOrder, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        AddWriteParams(cmd, manufacturer);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateAsync(Manufacturer manufacturer, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Manufacturer SET
                Name = @Name, Description = @Description, PictureId = @PictureId,
                Published = @Published, DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND Deleted = 0
            """;
        cmd.Parameters.AddWithValue("@Id", manufacturer.Id);
        AddWriteParams(cmd, manufacturer);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Manufacturer SET Deleted = 1 WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string BuildWhere(ManufacturerQuery q)
    {
        var parts = new List<string> { "Deleted = 0" };
        if (q.Published.HasValue) parts.Add("Published = @Published");
        return string.Join(" AND ", parts);
    }

    private static void AddFilterParams(SqlCommand cmd, ManufacturerQuery q)
    {
        if (q.Published.HasValue) cmd.Parameters.AddWithValue("@Published", q.Published.Value);
    }

    private static void AddWriteParams(SqlCommand cmd, Manufacturer m)
    {
        cmd.Parameters.AddWithValue("@Name", m.Name);
        cmd.Parameters.AddWithValue("@Description", (object?)m.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PictureId", m.PictureId);
        cmd.Parameters.AddWithValue("@Published", m.Published);
        cmd.Parameters.AddWithValue("@DisplayOrder", m.DisplayOrder);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", m.CreatedOnUtc);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", m.UpdatedOnUtc);
    }

    private static Manufacturer Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        PictureId = r.GetInt32(3),
        Published = r.GetBoolean(4),
        Deleted = r.GetBoolean(5),
        DisplayOrder = r.GetInt32(6),
        CreatedOnUtc = r.GetDateTime(7),
        UpdatedOnUtc = r.GetDateTime(8)
    };
}
