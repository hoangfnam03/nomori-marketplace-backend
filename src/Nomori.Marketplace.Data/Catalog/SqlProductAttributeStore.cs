using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

public sealed class SqlProductAttributeStore(IOptions<DatabaseOptions> options) : IProductAttributeStore
{
    // ---- Global attributes ----

    public async Task<IReadOnlyList<ProductAttributeSpec>> GetAllAttributesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Description, DisplayOrder FROM ProductAttribute ORDER BY DisplayOrder, Name";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProductAttributeSpec>();
        while (await reader.ReadAsync(ct)) list.Add(ReadAttribute(reader));
        return list;
    }

    public async Task<ProductAttributeSpec?> GetAttributeAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Description, DisplayOrder FROM ProductAttribute WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAttribute(reader) : null;
    }

    public async Task<int> InsertAttributeAsync(ProductAttributeSpec attr, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO ProductAttribute (Name, Description, DisplayOrder) OUTPUT INSERTED.Id VALUES (@Name, @Description, @DisplayOrder)";
        cmd.Parameters.AddWithValue("@Name", attr.Name);
        cmd.Parameters.AddWithValue("@Description", (object?)attr.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayOrder", attr.DisplayOrder);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateAttributeAsync(ProductAttributeSpec attr, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ProductAttribute SET Name = @Name, Description = @Description, DisplayOrder = @DisplayOrder WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", attr.Id);
        cmd.Parameters.AddWithValue("@Name", attr.Name);
        cmd.Parameters.AddWithValue("@Description", (object?)attr.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayOrder", attr.DisplayOrder);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAttributeAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ProductAttribute WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Mappings ----

    public async Task<IReadOnlyList<ProductAttributeMapping>> GetMappingsAsync(int productId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, ProductAttributeId, TextPrompt, IsRequired, ControlType, DisplayOrder FROM ProductAttributeMapping WHERE ProductId = @ProductId ORDER BY DisplayOrder";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProductAttributeMapping>();
        while (await reader.ReadAsync(ct)) list.Add(ReadMapping(reader));
        return list;
    }

    public async Task<ProductAttributeMapping?> GetMappingAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, ProductAttributeId, TextPrompt, IsRequired, ControlType, DisplayOrder FROM ProductAttributeMapping WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadMapping(reader) : null;
    }

    public async Task<int> InsertMappingAsync(ProductAttributeMapping mapping, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ProductAttributeMapping (ProductId, ProductAttributeId, TextPrompt, IsRequired, ControlType, DisplayOrder)
            OUTPUT INSERTED.Id
            VALUES (@ProductId, @ProductAttributeId, @TextPrompt, @IsRequired, @ControlType, @DisplayOrder)
            """;
        cmd.Parameters.AddWithValue("@ProductId", mapping.ProductId);
        cmd.Parameters.AddWithValue("@ProductAttributeId", mapping.ProductAttributeId);
        cmd.Parameters.AddWithValue("@TextPrompt", (object?)mapping.TextPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsRequired", mapping.IsRequired);
        cmd.Parameters.AddWithValue("@ControlType", (int)mapping.ControlType);
        cmd.Parameters.AddWithValue("@DisplayOrder", mapping.DisplayOrder);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateMappingAsync(ProductAttributeMapping mapping, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ProductAttributeMapping SET TextPrompt = @TextPrompt, IsRequired = @IsRequired, ControlType = @ControlType, DisplayOrder = @DisplayOrder WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", mapping.Id);
        cmd.Parameters.AddWithValue("@TextPrompt", (object?)mapping.TextPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsRequired", mapping.IsRequired);
        cmd.Parameters.AddWithValue("@ControlType", (int)mapping.ControlType);
        cmd.Parameters.AddWithValue("@DisplayOrder", mapping.DisplayOrder);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteMappingAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ProductAttributeMapping WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Values ----

    public async Task<IReadOnlyList<ProductAttributeValue>> GetValuesAsync(int mappingId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductAttributeMappingId, Name, ColorSquaresRgb, PriceAdjustment, IsPreSelected, DisplayOrder FROM ProductAttributeValue WHERE ProductAttributeMappingId = @MappingId ORDER BY DisplayOrder";
        cmd.Parameters.AddWithValue("@MappingId", mappingId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProductAttributeValue>();
        while (await reader.ReadAsync(ct)) list.Add(ReadValue(reader));
        return list;
    }

    public async Task<IReadOnlyList<ProductAttributeValue>> GetValuesByProductAsync(int productId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT v.Id, v.ProductAttributeMappingId, v.Name, v.ColorSquaresRgb, v.PriceAdjustment, v.IsPreSelected, v.DisplayOrder
            FROM ProductAttributeValue v
            INNER JOIN ProductAttributeMapping m ON m.Id = v.ProductAttributeMappingId
            WHERE m.ProductId = @ProductId
            ORDER BY m.DisplayOrder, v.DisplayOrder
            """;
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProductAttributeValue>();
        while (await reader.ReadAsync(ct)) list.Add(ReadValue(reader));
        return list;
    }

    public async Task<ProductAttributeValue?> GetValueAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductAttributeMappingId, Name, ColorSquaresRgb, PriceAdjustment, IsPreSelected, DisplayOrder FROM ProductAttributeValue WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadValue(reader) : null;
    }

    public async Task<int> InsertValueAsync(ProductAttributeValue value, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ProductAttributeValue (ProductAttributeMappingId, Name, ColorSquaresRgb, PriceAdjustment, IsPreSelected, DisplayOrder)
            OUTPUT INSERTED.Id
            VALUES (@MappingId, @Name, @ColorSquaresRgb, @PriceAdjustment, @IsPreSelected, @DisplayOrder)
            """;
        cmd.Parameters.AddWithValue("@MappingId", value.ProductAttributeMappingId);
        cmd.Parameters.AddWithValue("@Name", value.Name);
        cmd.Parameters.AddWithValue("@ColorSquaresRgb", (object?)value.ColorSquaresRgb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PriceAdjustment", value.PriceAdjustment);
        cmd.Parameters.AddWithValue("@IsPreSelected", value.IsPreSelected);
        cmd.Parameters.AddWithValue("@DisplayOrder", value.DisplayOrder);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateValueAsync(ProductAttributeValue value, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ProductAttributeValue SET Name = @Name, ColorSquaresRgb = @ColorSquaresRgb, PriceAdjustment = @PriceAdjustment, IsPreSelected = @IsPreSelected, DisplayOrder = @DisplayOrder WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", value.Id);
        cmd.Parameters.AddWithValue("@Name", value.Name);
        cmd.Parameters.AddWithValue("@ColorSquaresRgb", (object?)value.ColorSquaresRgb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PriceAdjustment", value.PriceAdjustment);
        cmd.Parameters.AddWithValue("@IsPreSelected", value.IsPreSelected);
        cmd.Parameters.AddWithValue("@DisplayOrder", value.DisplayOrder);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteValueAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ProductAttributeValue WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Combinations ----

    public async Task<IReadOnlyList<ProductAttributeCombination>> GetCombinationsAsync(int productId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, AttributesJson, StockQuantity, AllowOutOfStockOrders, Sku, OverriddenPrice FROM ProductAttributeCombination WHERE ProductId = @ProductId";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProductAttributeCombination>();
        while (await reader.ReadAsync(ct)) list.Add(ReadCombination(reader));
        return list;
    }

    public async Task<ProductAttributeCombination?> GetCombinationAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, AttributesJson, StockQuantity, AllowOutOfStockOrders, Sku, OverriddenPrice FROM ProductAttributeCombination WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCombination(reader) : null;
    }

    public async Task<int> InsertCombinationAsync(ProductAttributeCombination combo, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ProductAttributeCombination (ProductId, AttributesJson, StockQuantity, AllowOutOfStockOrders, Sku, OverriddenPrice)
            OUTPUT INSERTED.Id
            VALUES (@ProductId, @AttributesJson, @StockQuantity, @AllowOutOfStockOrders, @Sku, @OverriddenPrice)
            """;
        cmd.Parameters.AddWithValue("@ProductId", combo.ProductId);
        cmd.Parameters.AddWithValue("@AttributesJson", combo.AttributesJson);
        cmd.Parameters.AddWithValue("@StockQuantity", combo.StockQuantity);
        cmd.Parameters.AddWithValue("@AllowOutOfStockOrders", combo.AllowOutOfStockOrders);
        cmd.Parameters.AddWithValue("@Sku", (object?)combo.Sku ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OverriddenPrice", (object?)combo.OverriddenPrice ?? DBNull.Value);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateCombinationAsync(ProductAttributeCombination combo, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ProductAttributeCombination SET
                AttributesJson = @AttributesJson, StockQuantity = @StockQuantity,
                AllowOutOfStockOrders = @AllowOutOfStockOrders, Sku = @Sku, OverriddenPrice = @OverriddenPrice
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", combo.Id);
        cmd.Parameters.AddWithValue("@AttributesJson", combo.AttributesJson);
        cmd.Parameters.AddWithValue("@StockQuantity", combo.StockQuantity);
        cmd.Parameters.AddWithValue("@AllowOutOfStockOrders", combo.AllowOutOfStockOrders);
        cmd.Parameters.AddWithValue("@Sku", (object?)combo.Sku ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OverriddenPrice", (object?)combo.OverriddenPrice ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteCombinationAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ProductAttributeCombination WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Helpers ----

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(options.Value.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static ProductAttributeSpec ReadAttribute(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        DisplayOrder = r.GetInt32(3)
    };

    private static ProductAttributeMapping ReadMapping(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        ProductId = r.GetInt32(1),
        ProductAttributeId = r.GetInt32(2),
        TextPrompt = r.IsDBNull(3) ? null : r.GetString(3),
        IsRequired = r.GetBoolean(4),
        ControlType = (AttributeControlType)r.GetInt32(5),
        DisplayOrder = r.GetInt32(6)
    };

    private static ProductAttributeValue ReadValue(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        ProductAttributeMappingId = r.GetInt32(1),
        Name = r.GetString(2),
        ColorSquaresRgb = r.IsDBNull(3) ? null : r.GetString(3),
        PriceAdjustment = r.GetDecimal(4),
        IsPreSelected = r.GetBoolean(5),
        DisplayOrder = r.GetInt32(6)
    };

    public async Task<bool> IsCombinationSkuTakenAsync(int vendorId, string sku, int excludeProductId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        // Any product SKU of the shop (the product's own included), and combination SKUs of the shop's other products.
        cmd.CommandText = """
            SELECT TOP 1 1 FROM Product WHERE VendorId = @VendorId AND Sku = @Sku AND Deleted = 0
            UNION ALL
            SELECT TOP 1 1 FROM ProductAttributeCombination c INNER JOIN Product p ON p.Id = c.ProductId
            WHERE p.VendorId = @VendorId AND c.Sku = @Sku AND p.Deleted = 0 AND p.Id <> @ExcludeId
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@Sku", sku);
        cmd.Parameters.AddWithValue("@ExcludeId", excludeProductId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task ReplaceVariantsAsync(int productId, SaveVariantsCommand command, int? actorCustomerId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        async Task<int> InsertAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }

        var stockBefore = await InsertAsync("SELECT StockQuantity FROM Product WITH (UPDLOCK) WHERE Id = @P", ("@P", productId));

        // Values go with their mapping (cascade). Combinations are removed explicitly.
        await InsertAsync("DELETE FROM ProductAttributeCombination WHERE ProductId = @P; DELETE FROM ProductAttributeMapping WHERE ProductId = @P; SELECT 0",
            ("@P", productId));

        var valueIds = new List<int[]>();
        var mappingIds = new List<int>();
        for (var a = 0; a < command.Attributes.Count; a++)
        {
            var attribute = command.Attributes[a];
            var mappingId = await InsertAsync("""
                INSERT INTO ProductAttributeMapping (ProductId, ProductAttributeId, TextPrompt, IsRequired, ControlType, DisplayOrder)
                OUTPUT INSERTED.Id VALUES (@P, @AttributeId, NULL, @Required, @ControlType, @Order)
                """,
                ("@P", productId), ("@AttributeId", attribute.ProductAttributeId), ("@Required", attribute.IsRequired),
                ("@ControlType", (int)(attribute.Values.Any(v => v.ColorSquaresRgb is not null) ? AttributeControlType.ColorSquares : AttributeControlType.DropdownList)),
                ("@Order", a));
            mappingIds.Add(mappingId);

            var ids = new int[attribute.Values.Count];
            for (var v = 0; v < ids.Length; v++)
            {
                var value = attribute.Values[v];
                ids[v] = await InsertAsync("""
                    INSERT INTO ProductAttributeValue (ProductAttributeMappingId, Name, ColorSquaresRgb, PriceAdjustment, IsPreSelected, DisplayOrder)
                    OUTPUT INSERTED.Id VALUES (@MappingId, @Name, @Color, @Adjustment, 0, @Order)
                    """,
                    ("@MappingId", mappingId), ("@Name", value.Name), ("@Color", value.ColorSquaresRgb), ("@Adjustment", value.PriceAdjustment), ("@Order", v));
            }
            valueIds.Add(ids);
        }

        foreach (var combination in command.Combinations)
        {
            // The key maps each mapping id to the chosen value id.
            var key = new Dictionary<string, int>();
            for (var a = 0; a < mappingIds.Count; a++) key[mappingIds[a].ToString(System.Globalization.CultureInfo.InvariantCulture)] = valueIds[a][combination.ValueIndexes[a]];

            await InsertAsync("""
                INSERT INTO ProductAttributeCombination (ProductId, AttributesJson, StockQuantity, AllowOutOfStockOrders, Sku, OverriddenPrice)
                OUTPUT INSERTED.Id VALUES (@P, @Json, @Stock, 0, @Sku, @Price)
                """,
                ("@P", productId), ("@Json", System.Text.Json.JsonSerializer.Serialize(key)), ("@Stock", combination.StockQuantity),
                ("@Sku", combination.Sku), ("@Price", combination.OverriddenPrice));
        }

        if (command.Combinations.Count > 0)
        {
            var stockAfter = command.Combinations.Sum(c => c.StockQuantity);
            await InsertAsync("UPDATE Product SET StockQuantity = @Stock, UpdatedOnUtc = SYSUTCDATETIME() WHERE Id = @P; SELECT 0",
                ("@Stock", stockAfter), ("@P", productId));

            // The ledger records the change of the product total; the combinations themselves are new rows.
            if (stockAfter != stockBefore)
            {
                await InsertAsync("""
                    INSERT INTO StockMovement (ProductId, CombinationId, Delta, QuantityAfter, Reason, Reference, Note, ActorCustomerId, CreatedOnUtc)
                    OUTPUT INSERTED.Id VALUES (@P, NULL, @Delta, @After, 'variants_saved', NULL, NULL, @Actor, SYSUTCDATETIME())
                    """,
                    ("@P", productId), ("@Delta", stockAfter - stockBefore), ("@After", stockAfter), ("@Actor", actorCustomerId));
            }
        }

        await transaction.CommitAsync(ct);
    }

    private static ProductAttributeCombination ReadCombination(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        ProductId = r.GetInt32(1),
        AttributesJson = r.GetString(2),
        StockQuantity = r.GetInt32(3),
        AllowOutOfStockOrders = r.GetBoolean(4),
        Sku = r.IsDBNull(5) ? null : r.GetString(5),
        OverriddenPrice = r.IsDBNull(6) ? null : r.GetDecimal(6)
    };
}
