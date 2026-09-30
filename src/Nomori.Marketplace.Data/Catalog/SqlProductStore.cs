using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

public sealed class SqlProductStore(IOptions<DatabaseOptions> options) : IProductStore
{
    // Every read joins the owning shop so callers get its name and status without a second query.
    private const string SelectColumns =
        "p.Id, p.Name, p.ShortDescription, p.FullDescription, p.Price, p.OldPrice, p.StockQuantity, p.Published, p.Deleted, p.VendorId, p.ShowOnHomepage, p.DisplayOrder, p.CreatedOnUtc, p.UpdatedOnUtc, v.Name, v.Active";

    private const string FromClause = "FROM Product p INNER JOIN Vendor v ON v.Id = p.VendorId";

    public async Task<Product?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} {FromClause} WHERE p.Id = @Id AND p.Deleted = 0";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<(IReadOnlyList<Product> Items, int TotalCount)> GetPagedAsync(ProductQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var (where, sort) = BuildFilter(query);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) {FromClause} WHERE {where}";
        AddFilterParams(countCmd, query);
        var totalCount = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var cmd = connection.CreateCommand();
        var offset = (query.Page - 1) * query.PageSize;
        cmd.CommandText = $"SELECT {SelectColumns} {FromClause} WHERE {where} ORDER BY {sort} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
        AddFilterParams(cmd, query);
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@PageSize", query.PageSize);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Product>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return (items, totalCount);
    }

    public async Task<IReadOnlyList<int>> GetCategoryIdsAsync(int productId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CategoryId FROM ProductCategory WHERE ProductId = @ProductId ORDER BY DisplayOrder";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<IReadOnlyList<int>> GetManufacturerIdsAsync(int productId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ManufacturerId FROM ProductManufacturer WHERE ProductId = @ProductId ORDER BY DisplayOrder";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<int> InsertAsync(Product product, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Product (Name, ShortDescription, FullDescription, Price, OldPrice, StockQuantity, Published, Deleted, VendorId, ShowOnHomepage, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Name, @ShortDescription, @FullDescription, @Price, @OldPrice, @StockQuantity, @Published, 0, @VendorId, @ShowOnHomepage, @DisplayOrder, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        AddWriteParams(cmd, product);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateAsync(Product product, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Product SET
                Name = @Name, ShortDescription = @ShortDescription, FullDescription = @FullDescription,
                Price = @Price, OldPrice = @OldPrice, StockQuantity = @StockQuantity,
                Published = @Published, ShowOnHomepage = @ShowOnHomepage,
                DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND Deleted = 0
            """;
        // The owner is deliberately not updated here: only SetVendorAsync (an admin transfer) may change it.
        cmd.Parameters.AddWithValue("@Id", product.Id);
        AddWriteParams(cmd, product);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetVendorAsync(int productId, int vendorId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Product SET VendorId = @VendorId, UpdatedOnUtc = @NowUtc WHERE Id = @Id AND Deleted = 0";
        cmd.Parameters.AddWithValue("@Id", productId);
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@NowUtc", nowUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Product SET Deleted = 1 WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task SetCategoriesAsync(int productId, int[] categoryIds, CancellationToken cancellationToken) =>
        ReplaceMappingsAsync("ProductCategory", "CategoryId", productId, categoryIds, cancellationToken);

    public Task SetManufacturersAsync(int productId, int[] manufacturerIds, CancellationToken cancellationToken) =>
        ReplaceMappingsAsync("ProductManufacturer", "ManufacturerId", productId, manufacturerIds, cancellationToken);

    /// <summary>Replaces all mappings of one product in a single transaction. Table and column names are constants, never user input.</summary>
    private async Task ReplaceMappingsAsync(string table, string column, int productId, int[] ids, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var deleteCmd = connection.CreateCommand();
        deleteCmd.Transaction = transaction;
        deleteCmd.CommandText = $"DELETE FROM {table} WHERE ProductId = @ProductId";
        deleteCmd.Parameters.AddWithValue("@ProductId", productId);
        await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

        for (var i = 0; i < ids.Length; i++)
        {
            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = $"INSERT INTO {table} (ProductId, {column}, IsFeaturedProduct, DisplayOrder) VALUES (@ProductId, @Id, 0, @DisplayOrder)";
            insertCmd.Parameters.AddWithValue("@ProductId", productId);
            insertCmd.Parameters.AddWithValue("@Id", ids[i]);
            insertCmd.Parameters.AddWithValue("@DisplayOrder", i);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static (string Where, string Sort) BuildFilter(ProductQuery q)
    {
        var parts = new List<string> { "p.Deleted = 0" };
        if (q.CategoryId.HasValue)
            parts.Add("p.Id IN (SELECT ProductId FROM ProductCategory WHERE CategoryId = @CategoryId)");
        if (q.ManufacturerId.HasValue)
            parts.Add("p.Id IN (SELECT ProductId FROM ProductManufacturer WHERE ManufacturerId = @ManufacturerId)");
        if (q.MinPrice.HasValue) parts.Add("p.Price >= @MinPrice");
        if (q.MaxPrice.HasValue) parts.Add("p.Price <= @MaxPrice");
        if (!string.IsNullOrWhiteSpace(q.Search)) parts.Add("(p.Name LIKE @Search OR p.ShortDescription LIKE @Search)");
        if (q.Published.HasValue) parts.Add("p.Published = @Published");
        if (q.VendorId.HasValue) parts.Add("p.VendorId = @VendorId");
        // Products of deactivated or deleted shops are not shown to the public.
        if (q.OnlyActiveShops) parts.Add("v.Active = 1 AND v.Deleted = 0");

        var sort = q.Sort switch
        {
            ProductSortOrder.NameAsc => "p.Name ASC",
            ProductSortOrder.NameDesc => "p.Name DESC",
            ProductSortOrder.PriceAsc => "p.Price ASC",
            ProductSortOrder.PriceDesc => "p.Price DESC",
            ProductSortOrder.Newest => "p.CreatedOnUtc DESC",
            _ => "p.DisplayOrder ASC, p.Name ASC"
        };

        return (string.Join(" AND ", parts), sort);
    }

    private static void AddFilterParams(SqlCommand cmd, ProductQuery q)
    {
        if (q.CategoryId.HasValue) cmd.Parameters.AddWithValue("@CategoryId", q.CategoryId.Value);
        if (q.ManufacturerId.HasValue) cmd.Parameters.AddWithValue("@ManufacturerId", q.ManufacturerId.Value);
        if (q.MinPrice.HasValue) cmd.Parameters.AddWithValue("@MinPrice", q.MinPrice.Value);
        if (q.MaxPrice.HasValue) cmd.Parameters.AddWithValue("@MaxPrice", q.MaxPrice.Value);
        if (!string.IsNullOrWhiteSpace(q.Search)) cmd.Parameters.AddWithValue("@Search", $"%{q.Search}%");
        if (q.Published.HasValue) cmd.Parameters.AddWithValue("@Published", q.Published.Value);
        if (q.VendorId.HasValue) cmd.Parameters.AddWithValue("@VendorId", q.VendorId.Value);
    }

    private static void AddWriteParams(SqlCommand cmd, Product p)
    {
        cmd.Parameters.AddWithValue("@Name", p.Name);
        cmd.Parameters.AddWithValue("@ShortDescription", (object?)p.ShortDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FullDescription", (object?)p.FullDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Price", p.Price);
        cmd.Parameters.AddWithValue("@OldPrice", p.OldPrice);
        cmd.Parameters.AddWithValue("@StockQuantity", p.StockQuantity);
        cmd.Parameters.AddWithValue("@Published", p.Published);
        cmd.Parameters.AddWithValue("@VendorId", p.VendorId);
        cmd.Parameters.AddWithValue("@ShowOnHomepage", p.ShowOnHomepage);
        cmd.Parameters.AddWithValue("@DisplayOrder", p.DisplayOrder);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", p.CreatedOnUtc);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", p.UpdatedOnUtc);
    }

    private static Product Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        ShortDescription = r.IsDBNull(2) ? null : r.GetString(2),
        FullDescription = r.IsDBNull(3) ? null : r.GetString(3),
        Price = r.GetDecimal(4),
        OldPrice = r.GetDecimal(5),
        StockQuantity = r.GetInt32(6),
        Published = r.GetBoolean(7),
        Deleted = r.GetBoolean(8),
        VendorId = r.GetInt32(9),
        ShowOnHomepage = r.GetBoolean(10),
        DisplayOrder = r.GetInt32(11),
        CreatedOnUtc = r.GetDateTime(12),
        UpdatedOnUtc = r.GetDateTime(13),
        VendorName = r.GetString(14),
        VendorActive = r.GetBoolean(15)
    };
}
