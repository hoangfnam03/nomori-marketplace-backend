using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

public sealed class SqlCategoryStore(IOptions<DatabaseOptions> options) : ICategoryStore
{
    private const string SelectColumns =
        "Id, Name, Description, ParentCategoryId, PictureId, ShowOnHomepage, Published, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc";

    public async Task<Category?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Category WHERE Id = @Id AND Deleted = 0";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<(IReadOnlyList<Category> Items, int TotalCount)> GetPagedAsync(CategoryQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var (where, _) = BuildWhere(query);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM Category WHERE {where}";
        AddFilterParams(countCmd, query);
        var totalCount = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var cmd = connection.CreateCommand();
        var offset = (query.Page - 1) * query.PageSize;
        cmd.CommandText = $"SELECT {SelectColumns} FROM Category WHERE {where} ORDER BY DisplayOrder, Name OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
        AddFilterParams(cmd, query);
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@PageSize", query.PageSize);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Category>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return (items, totalCount);
    }

    public async Task<IReadOnlyList<Category>> GetAllPublishedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Category WHERE Published = 1 AND Deleted = 0 ORDER BY DisplayOrder, Name";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Category>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async Task<int> InsertAsync(Category category, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Category (Name, Description, ParentCategoryId, PictureId, ShowOnHomepage, Published, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Name, @Description, @ParentCategoryId, @PictureId, @ShowOnHomepage, @Published, 0, @DisplayOrder, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        AddWriteParams(cmd, category);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Category SET
                Name = @Name, Description = @Description, ParentCategoryId = @ParentCategoryId,
                PictureId = @PictureId, ShowOnHomepage = @ShowOnHomepage, Published = @Published,
                DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND Deleted = 0
            """;
        cmd.Parameters.AddWithValue("@Id", category.Id);
        AddWriteParams(cmd, category);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Category SET Deleted = 1 WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static (string Where, bool HasParams) BuildWhere(CategoryQuery q)
    {
        var parts = new List<string> { "Deleted = 0" };
        if (q.ParentId.HasValue) parts.Add("ParentCategoryId = @ParentCategoryId");
        if (q.Published.HasValue) parts.Add("Published = @Published");
        return (string.Join(" AND ", parts), parts.Count > 1);
    }

    private static void AddFilterParams(SqlCommand cmd, CategoryQuery q)
    {
        if (q.ParentId.HasValue) cmd.Parameters.AddWithValue("@ParentCategoryId", q.ParentId.Value);
        if (q.Published.HasValue) cmd.Parameters.AddWithValue("@Published", q.Published.Value);
    }

    private static void AddWriteParams(SqlCommand cmd, Category c)
    {
        cmd.Parameters.AddWithValue("@Name", c.Name);
        cmd.Parameters.AddWithValue("@Description", (object?)c.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ParentCategoryId", c.ParentCategoryId);
        cmd.Parameters.AddWithValue("@PictureId", c.PictureId);
        cmd.Parameters.AddWithValue("@ShowOnHomepage", c.ShowOnHomepage);
        cmd.Parameters.AddWithValue("@Published", c.Published);
        cmd.Parameters.AddWithValue("@DisplayOrder", c.DisplayOrder);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", c.CreatedOnUtc);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", c.UpdatedOnUtc);
    }

    private static Category Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        ParentCategoryId = r.GetInt32(3),
        PictureId = r.GetInt32(4),
        ShowOnHomepage = r.GetBoolean(5),
        Published = r.GetBoolean(6),
        Deleted = r.GetBoolean(7),
        DisplayOrder = r.GetInt32(8),
        CreatedOnUtc = r.GetDateTime(9),
        UpdatedOnUtc = r.GetDateTime(10)
    };
}
