using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

public sealed class SqlCategoryStore(IOptions<DatabaseOptions> options) : ICategoryStore
{
    private const string SelectColumns =
        "Id, Name, Description, ParentCategoryId, PictureId, ShowOnHomepage, Published, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc, RestrictFromVendors";

    // Categories that are published and have no unpublished or deleted ancestor. Loops are impossible (validated) but the recursion is capped anyway.
    private const string VisibleCte = """
        WITH Visible AS (
            SELECT Id FROM Category WHERE ParentCategoryId = 0 AND Published = 1 AND Deleted = 0
            UNION ALL
            SELECT c.Id FROM Category c INNER JOIN Visible v ON c.ParentCategoryId = v.Id WHERE c.Published = 1 AND c.Deleted = 0
        )
        """;

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
        countCmd.CommandText = $"{Prefix(query)}SELECT COUNT(*) FROM Category WHERE {where}{Suffix(query)}";
        AddFilterParams(countCmd, query);
        var totalCount = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var cmd = connection.CreateCommand();
        var offset = (query.Page - 1) * query.PageSize;
        cmd.CommandText = $"{Prefix(query)}SELECT {SelectColumns} FROM Category WHERE {where} ORDER BY DisplayOrder, Name OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY{Suffix(query)}";
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

    public async Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Category WHERE Deleted = 0 ORDER BY DisplayOrder, Name";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Category>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async Task<int> CountChildrenAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Category WHERE ParentCategoryId = @Id AND Deleted = 0";
        cmd.Parameters.AddWithValue("@Id", id);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<int> CountProductsAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ProductCategory pc INNER JOIN Product p ON p.Id = pc.ProductId AND p.Deleted = 0 WHERE pc.CategoryId = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> NameExistsAsync(string name, int parentCategoryId, int? excludeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM Category
                WHERE Deleted = 0 AND ParentCategoryId = @ParentId AND Name = @Name AND (@ExcludeId IS NULL OR Id <> @ExcludeId)
            ) THEN 1 ELSE 0 END
            """;
        cmd.Parameters.AddWithValue("@ParentId", parentCategoryId);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@ExcludeId", (object?)excludeId ?? DBNull.Value);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    public async Task<int> InsertAsync(Category category, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Category (Name, Description, ParentCategoryId, PictureId, ShowOnHomepage, Published, RestrictFromVendors, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Name, @Description, @ParentCategoryId, @PictureId, @ShowOnHomepage, @Published, @RestrictFromVendors, 0, @DisplayOrder, @CreatedOnUtc, @UpdatedOnUtc)
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
                RestrictFromVendors = @RestrictFromVendors, DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
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
        // Public lists: a category is only visible when all its ancestors are published too.
        if (q.Published == true) parts.Add("Id IN (SELECT Id FROM Visible)");
        return (string.Join(" AND ", parts), parts.Count > 1);
    }

    private static string Prefix(CategoryQuery q) => q.Published == true ? VisibleCte + "\n" : string.Empty;

    private static string Suffix(CategoryQuery q) => q.Published == true ? " OPTION (MAXRECURSION 100)" : string.Empty;

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
        cmd.Parameters.AddWithValue("@RestrictFromVendors", c.RestrictFromVendors);
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
        UpdatedOnUtc = r.GetDateTime(10),
        RestrictFromVendors = r.GetBoolean(11)
    };
}
