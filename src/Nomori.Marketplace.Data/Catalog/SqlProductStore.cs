using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

public sealed class SqlProductStore(IOptions<DatabaseOptions> options) : IProductStore
{
    // Every read joins the owning shop so callers get its name and status without a second query.
    private const string SelectColumns =
        "p.Id, p.Name, p.ShortDescription, p.FullDescription, p.Price, p.OldPrice, p.StockQuantity, p.Status, p.Deleted, p.VendorId, p.ShowOnHomepage, p.DisplayOrder, p.CreatedOnUtc, p.UpdatedOnUtc, v.Name, v.Active, p.StatusBeforeHidden, p.HiddenReason, p.HiddenOnUtc, p.HiddenByCustomerId, p.ReviewRequestedOnUtc, p.Sku, p.Gtin, p.ManufacturerPartNumber, p.AvailableStartUtc, p.AvailableEndUtc, p.TrackInventory, p.LowStockThreshold";

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
            INSERT INTO Product (Name, ShortDescription, FullDescription, Price, OldPrice, StockQuantity, Status, Deleted, VendorId, ShowOnHomepage, DisplayOrder, CreatedOnUtc, UpdatedOnUtc, Sku, Gtin, ManufacturerPartNumber, AvailableStartUtc, AvailableEndUtc)
            OUTPUT INSERTED.Id
            VALUES (@Name, @ShortDescription, @FullDescription, @Price, @OldPrice, @StockQuantity, @Status, 0, @VendorId, @ShowOnHomepage, @DisplayOrder, @CreatedOnUtc, @UpdatedOnUtc, @Sku, @Gtin, @ManufacturerPartNumber, @AvailableStartUtc, @AvailableEndUtc)
            """;
        AddWriteParams(cmd, product);
        AddContentParams(cmd, product);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateAsync(Product product, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Product SET
                Name = @Name, ShortDescription = @ShortDescription, FullDescription = @FullDescription,
                Price = @Price, OldPrice = @OldPrice,
                ShowOnHomepage = @ShowOnHomepage,
                DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND Deleted = 0
            """;
        // The owner, the lifecycle fields, the content fields and the stock are deliberately not updated here: see SetVendorAsync, UpdateLifecycleAsync,
        // UpdateContentAsync and the inventory store (stock only changes through the ledger).
        cmd.Parameters.AddWithValue("@Id", product.Id);
        AddWriteParams(cmd, product);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateContentAsync(Product product, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Product SET
                Sku = @Sku, Gtin = @Gtin, ManufacturerPartNumber = @ManufacturerPartNumber,
                AvailableStartUtc = @AvailableStartUtc, AvailableEndUtc = @AvailableEndUtc
            WHERE Id = @Id AND Deleted = 0
            """;
        cmd.Parameters.AddWithValue("@Id", product.Id);
        AddContentParams(cmd, product);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasVariantsAsync(int productId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 1 FROM ProductAttributeCombination WHERE ProductId = @ProductId";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> IsSkuTakenAsync(int vendorId, string sku, int excludeProductId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // A combination SKU of another product of the shop counts too.
        cmd.CommandText = """
            SELECT TOP 1 1 FROM Product WHERE VendorId = @VendorId AND Sku = @Sku AND Deleted = 0 AND Id <> @ExcludeId
            UNION ALL
            SELECT TOP 1 1 FROM ProductAttributeCombination c INNER JOIN Product p ON p.Id = c.ProductId
            WHERE p.VendorId = @VendorId AND c.Sku = @Sku AND p.Deleted = 0 AND p.Id <> @ExcludeId
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@Sku", sku);
        cmd.Parameters.AddWithValue("@ExcludeId", excludeProductId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<int>> GetRelatedIdsAsync(int productId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT RelatedProductId FROM ProductRelation WHERE ProductId = @ProductId ORDER BY DisplayOrder, RelatedProductId";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task SetRelatedAsync(int productId, int[] relatedProductIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM ProductRelation WHERE ProductId = @ProductId";
            deleteCmd.Parameters.AddWithValue("@ProductId", productId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var i = 0; i < relatedProductIds.Length; i++)
        {
            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO ProductRelation (ProductId, RelatedProductId, DisplayOrder) VALUES (@ProductId, @RelatedId, @DisplayOrder)";
            insertCmd.Parameters.AddWithValue("@ProductId", productId);
            insertCmd.Parameters.AddWithValue("@RelatedId", relatedProductIds[i]);
            insertCmd.Parameters.AddWithValue("@DisplayOrder", i);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateLifecycleAsync(Product product, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Product SET
                Status = @Status, StatusBeforeHidden = @StatusBeforeHidden, HiddenReason = @HiddenReason,
                HiddenOnUtc = @HiddenOnUtc, HiddenByCustomerId = @HiddenBy, ReviewRequestedOnUtc = @ReviewRequestedOnUtc,
                UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND Deleted = 0
            """;
        cmd.Parameters.AddWithValue("@Id", product.Id);
        cmd.Parameters.AddWithValue("@Status", (int)product.Status);
        cmd.Parameters.AddWithValue("@StatusBeforeHidden", product.StatusBeforeHidden is { } before ? (int)before : DBNull.Value);
        cmd.Parameters.AddWithValue("@HiddenReason", (object?)product.HiddenReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@HiddenOnUtc", (object?)product.HiddenOnUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@HiddenBy", (object?)product.HiddenByCustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReviewRequestedOnUtc", (object?)product.ReviewRequestedOnUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", product.UpdatedOnUtc);
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
        // A deleted product releases its pictures so the images can be reused or deleted.
        cmd.CommandText = "DELETE FROM ProductPicture WHERE ProductId = @Id; UPDATE Product SET Deleted = 1 WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task SetCategoriesAsync(int productId, int[] categoryIds, CancellationToken cancellationToken) =>
        ReplaceMappingsAsync("ProductCategory", "CategoryId", productId, categoryIds, cancellationToken);

    public async Task<IReadOnlyList<int>> GetPictureIdsAsync(int productId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MediaAssetId FROM ProductPicture WHERE ProductId = @ProductId ORDER BY DisplayOrder, MediaAssetId";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<IReadOnlyDictionary<int, int>> GetMainPictureIdsAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, int>();
        if (productIds.Count == 0) return result;

        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // Parameter names are generated (@p0, @p1, ...), never user text.
        var names = productIds.Select((_, i) => $"@p{i}").ToArray();
        cmd.CommandText = $"""
            SELECT ProductId, MediaAssetId FROM (
                SELECT ProductId, MediaAssetId, ROW_NUMBER() OVER (PARTITION BY ProductId ORDER BY DisplayOrder, MediaAssetId) AS Rn
                FROM ProductPicture WHERE ProductId IN ({string.Join(",", names)})) x
            WHERE Rn = 1
            """;
        var index = 0;
        foreach (var id in productIds) cmd.Parameters.AddWithValue(names[index++], id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetInt32(0)] = reader.GetInt32(1);
        return result;
    }

    public async Task<int?> GetPictureOwnerAsync(int mediaAssetId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ProductId FROM ProductPicture WHERE MediaAssetId = @Id";
        cmd.Parameters.AddWithValue("@Id", mediaAssetId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is int productId ? productId : null;
    }

    public async Task SetPicturesAsync(int productId, int[] mediaAssetIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM ProductPicture WHERE ProductId = @ProductId";
            deleteCmd.Parameters.AddWithValue("@ProductId", productId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var i = 0; i < mediaAssetIds.Length; i++)
        {
            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO ProductPicture (MediaAssetId, ProductId, DisplayOrder) VALUES (@Id, @ProductId, @DisplayOrder)";
            insertCmd.Parameters.AddWithValue("@Id", mediaAssetIds[i]);
            insertCmd.Parameters.AddWithValue("@ProductId", productId);
            insertCmd.Parameters.AddWithValue("@DisplayOrder", i);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

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
        if (!string.IsNullOrWhiteSpace(q.Search)) parts.Add("(p.Name LIKE @Search OR p.ShortDescription LIKE @Search OR p.Sku LIKE @Search)");
        if (q.Published.HasValue) parts.Add("p.Published = @Published");
        if (q.VendorId.HasValue) parts.Add("p.VendorId = @VendorId");
        if (q.Status.HasValue) parts.Add("p.Status = @Status");
        if (q.ReviewRequested == true) parts.Add("p.ReviewRequestedOnUtc IS NOT NULL");
        if (q.LowStock == true) parts.Add("p.TrackInventory = 1 AND p.StockQuantity <= p.LowStockThreshold");
        // Products of deactivated or deleted shops are not shown to the public.
        if (q.OnlyActiveShops)
        {
            parts.Add("v.Active = 1 AND v.Deleted = 0");
            // The publication window applies to every public read.
            parts.Add("(p.AvailableStartUtc IS NULL OR p.AvailableStartUtc <= SYSUTCDATETIME()) AND (p.AvailableEndUtc IS NULL OR p.AvailableEndUtc > SYSUTCDATETIME())");
        }

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
        if (q.Status.HasValue) cmd.Parameters.AddWithValue("@Status", (int)q.Status.Value);
    }

    private static void AddContentParams(SqlCommand cmd, Product p)
    {
        cmd.Parameters.AddWithValue("@Sku", (object?)p.Sku ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Gtin", (object?)p.Gtin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ManufacturerPartNumber", (object?)p.ManufacturerPartNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AvailableStartUtc", (object?)p.AvailableStartUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AvailableEndUtc", (object?)p.AvailableEndUtc ?? DBNull.Value);
    }

    private static void AddWriteParams(SqlCommand cmd, Product p)
    {
        cmd.Parameters.AddWithValue("@Name", p.Name);
        cmd.Parameters.AddWithValue("@ShortDescription", (object?)p.ShortDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FullDescription", (object?)p.FullDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Price", p.Price);
        cmd.Parameters.AddWithValue("@OldPrice", p.OldPrice);
        cmd.Parameters.AddWithValue("@StockQuantity", p.StockQuantity);
        cmd.Parameters.AddWithValue("@Status", (int)p.Status);
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
        Status = (ProductStatus)r.GetInt32(7),
        Deleted = r.GetBoolean(8),
        VendorId = r.GetInt32(9),
        ShowOnHomepage = r.GetBoolean(10),
        DisplayOrder = r.GetInt32(11),
        CreatedOnUtc = r.GetDateTime(12),
        UpdatedOnUtc = r.GetDateTime(13),
        VendorName = r.GetString(14),
        VendorActive = r.GetBoolean(15),
        StatusBeforeHidden = r.IsDBNull(16) ? null : (ProductStatus)r.GetInt32(16),
        HiddenReason = r.IsDBNull(17) ? null : r.GetString(17),
        HiddenOnUtc = r.IsDBNull(18) ? null : r.GetDateTime(18),
        HiddenByCustomerId = r.IsDBNull(19) ? null : r.GetInt32(19),
        ReviewRequestedOnUtc = r.IsDBNull(20) ? null : r.GetDateTime(20),
        Sku = r.IsDBNull(21) ? null : r.GetString(21),
        Gtin = r.IsDBNull(22) ? null : r.GetString(22),
        ManufacturerPartNumber = r.IsDBNull(23) ? null : r.GetString(23),
        AvailableStartUtc = r.IsDBNull(24) ? null : r.GetDateTime(24),
        AvailableEndUtc = r.IsDBNull(25) ? null : r.GetDateTime(25),
        TrackInventory = r.GetBoolean(26),
        LowStockThreshold = r.GetInt32(27)
    };
}
