using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

public sealed class SqlProductStore(IOptions<DatabaseOptions> options) : IProductStore
{
    private const string SelectColumns =
        "Id, Name, ShortDescription, FullDescription, Price, OldPrice, StockQuantity, Published, Deleted, VendorId, ShowOnHomepage, DisplayOrder, CreatedOnUtc, UpdatedOnUtc";

    public async Task<Product?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Product WHERE Id = @Id AND Deleted = 0";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<(IReadOnlyList<Product> Items, int TotalCount)> GetPagedAsync(ProductQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var (where, sort) = BuildFilter(query);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM Product WHERE {where}";
        AddFilterParams(countCmd, query);
        var totalCount = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var cmd = connection.CreateCommand();
        var offset = (query.Page - 1) * query.PageSize;
        cmd.CommandText = $"SELECT {SelectColumns} FROM Product WHERE {where} ORDER BY {sort} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
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
                Published = @Published, VendorId = @VendorId, ShowOnHomepage = @ShowOnHomepage,
                DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND Deleted = 0
            """;
        cmd.Parameters.AddWithValue("@Id", product.Id);
        AddWriteParams(cmd, product);
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

    public async Task SetCategoriesAsync(int productId, int[] categoryIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM ProductCategory WHERE ProductId = @ProductId";
        deleteCmd.Parameters.AddWithValue("@ProductId", productId);
        await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

        for (var i = 0; i < categoryIds.Length; i++)
        {
            await using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO ProductCategory (ProductId, CategoryId, IsFeaturedProduct, DisplayOrder) VALUES (@ProductId, @CategoryId, 0, @DisplayOrder)";
            insertCmd.Parameters.AddWithValue("@ProductId", productId);
            insertCmd.Parameters.AddWithValue("@CategoryId", categoryIds[i]);
            insertCmd.Parameters.AddWithValue("@DisplayOrder", i);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task SetManufacturersAsync(int productId, int[] manufacturerIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM ProductManufacturer WHERE ProductId = @ProductId";
        deleteCmd.Parameters.AddWithValue("@ProductId", productId);
        await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

        for (var i = 0; i < manufacturerIds.Length; i++)
        {
            await using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO ProductManufacturer (ProductId, ManufacturerId, IsFeaturedProduct, DisplayOrder) VALUES (@ProductId, @ManufacturerId, 0, @DisplayOrder)";
            insertCmd.Parameters.AddWithValue("@ProductId", productId);
            insertCmd.Parameters.AddWithValue("@ManufacturerId", manufacturerIds[i]);
            insertCmd.Parameters.AddWithValue("@DisplayOrder", i);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static (string Where, string Sort) BuildFilter(ProductQuery q)
    {
        var parts = new List<string> { "Deleted = 0" };
        if (q.CategoryId.HasValue)
            parts.Add("Id IN (SELECT ProductId FROM ProductCategory WHERE CategoryId = @CategoryId)");
        if (q.ManufacturerId.HasValue)
            parts.Add("Id IN (SELECT ProductId FROM ProductManufacturer WHERE ManufacturerId = @ManufacturerId)");
        if (q.MinPrice.HasValue) parts.Add("Price >= @MinPrice");
        if (q.MaxPrice.HasValue) parts.Add("Price <= @MaxPrice");
        if (!string.IsNullOrWhiteSpace(q.Search)) parts.Add("(Name LIKE @Search OR ShortDescription LIKE @Search)");
        if (q.Published.HasValue) parts.Add("Published = @Published");

        var sort = q.Sort switch
        {
            ProductSortOrder.NameAsc => "Name ASC",
            ProductSortOrder.NameDesc => "Name DESC",
            ProductSortOrder.PriceAsc => "Price ASC",
            ProductSortOrder.PriceDesc => "Price DESC",
            ProductSortOrder.Newest => "CreatedOnUtc DESC",
            _ => "DisplayOrder ASC, Name ASC"
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
        cmd.Parameters.AddWithValue("@VendorId", (object?)p.VendorId ?? DBNull.Value);
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
        VendorId = r.IsDBNull(9) ? null : r.GetInt32(9),
        ShowOnHomepage = r.GetBoolean(10),
        DisplayOrder = r.GetInt32(11),
        CreatedOnUtc = r.GetDateTime(12),
        UpdatedOnUtc = r.GetDateTime(13)
    };
}
