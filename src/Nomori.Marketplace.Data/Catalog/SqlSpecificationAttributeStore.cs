using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

public sealed class SqlSpecificationAttributeStore(IOptions<DatabaseOptions> options) : ISpecificationAttributeStore
{
    // ---- Groups ----

    public async Task<IReadOnlyList<SpecificationAttributeGroup>> GetGroupsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DisplayOrder FROM SpecificationAttributeGroup ORDER BY DisplayOrder, Name";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SpecificationAttributeGroup>();
        while (await r.ReadAsync(ct)) list.Add(new() { Id = r.GetInt32(0), Name = r.GetString(1), DisplayOrder = r.GetInt32(2) });
        return list;
    }

    public async Task<SpecificationAttributeGroup?> GetGroupAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DisplayOrder FROM SpecificationAttributeGroup WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new() { Id = r.GetInt32(0), Name = r.GetString(1), DisplayOrder = r.GetInt32(2) } : null;
    }

    public async Task<int> InsertGroupAsync(SpecificationAttributeGroup g, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO SpecificationAttributeGroup (Name, DisplayOrder) OUTPUT INSERTED.Id VALUES (@Name, @DisplayOrder)";
        cmd.Parameters.AddWithValue("@Name", g.Name);
        cmd.Parameters.AddWithValue("@DisplayOrder", g.DisplayOrder);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateGroupAsync(SpecificationAttributeGroup g, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE SpecificationAttributeGroup SET Name = @Name, DisplayOrder = @DisplayOrder WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", g.Id);
        cmd.Parameters.AddWithValue("@Name", g.Name);
        cmd.Parameters.AddWithValue("@DisplayOrder", g.DisplayOrder);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteGroupAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SpecificationAttributeGroup WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Spec attributes ----

    public async Task<IReadOnlyList<SpecificationAttributeDef>> GetAllAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, SpecificationAttributeGroupId, DisplayOrder FROM SpecificationAttribute ORDER BY DisplayOrder, Name";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SpecificationAttributeDef>();
        while (await r.ReadAsync(ct)) list.Add(ReadSpecAttr(r));
        return list;
    }

    public async Task<SpecificationAttributeDef?> GetAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, SpecificationAttributeGroupId, DisplayOrder FROM SpecificationAttribute WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSpecAttr(r) : null;
    }

    public async Task<int> InsertAsync(SpecificationAttributeDef attr, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO SpecificationAttribute (Name, SpecificationAttributeGroupId, DisplayOrder) OUTPUT INSERTED.Id VALUES (@Name, @GroupId, @DisplayOrder)";
        cmd.Parameters.AddWithValue("@Name", attr.Name);
        cmd.Parameters.AddWithValue("@GroupId", (object?)attr.SpecificationAttributeGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayOrder", attr.DisplayOrder);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateAsync(SpecificationAttributeDef attr, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE SpecificationAttribute SET Name = @Name, SpecificationAttributeGroupId = @GroupId, DisplayOrder = @DisplayOrder WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", attr.Id);
        cmd.Parameters.AddWithValue("@Name", attr.Name);
        cmd.Parameters.AddWithValue("@GroupId", (object?)attr.SpecificationAttributeGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayOrder", attr.DisplayOrder);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SpecificationAttribute WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Options ----

    public async Task<IReadOnlyList<SpecificationAttributeOption>> GetOptionsAsync(int specAttributeId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, SpecificationAttributeId, Name, ColorSquaresRgb, DisplayOrder FROM SpecificationAttributeOption WHERE SpecificationAttributeId = @AttrId ORDER BY DisplayOrder";
        cmd.Parameters.AddWithValue("@AttrId", specAttributeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SpecificationAttributeOption>();
        while (await r.ReadAsync(ct)) list.Add(ReadOption(r));
        return list;
    }

    public async Task<IReadOnlyList<SpecificationAttributeOption>> GetOptionsByIdsAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var paramNames = idList.Select((_, i) => $"@Id{i}").ToArray();
        cmd.CommandText = $"SELECT Id, SpecificationAttributeId, Name, ColorSquaresRgb, DisplayOrder FROM SpecificationAttributeOption WHERE Id IN ({string.Join(',', paramNames)})";
        for (var i = 0; i < idList.Count; i++) cmd.Parameters.AddWithValue(paramNames[i], idList[i]);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SpecificationAttributeOption>();
        while (await r.ReadAsync(ct)) list.Add(ReadOption(r));
        return list;
    }

    public async Task<SpecificationAttributeOption?> GetOptionAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, SpecificationAttributeId, Name, ColorSquaresRgb, DisplayOrder FROM SpecificationAttributeOption WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadOption(r) : null;
    }

    public async Task<int> InsertOptionAsync(SpecificationAttributeOption opt, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO SpecificationAttributeOption (SpecificationAttributeId, Name, ColorSquaresRgb, DisplayOrder) OUTPUT INSERTED.Id VALUES (@AttrId, @Name, @Rgb, @DisplayOrder)";
        cmd.Parameters.AddWithValue("@AttrId", opt.SpecificationAttributeId);
        cmd.Parameters.AddWithValue("@Name", opt.Name);
        cmd.Parameters.AddWithValue("@Rgb", (object?)opt.ColorSquaresRgb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayOrder", opt.DisplayOrder);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateOptionAsync(SpecificationAttributeOption opt, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE SpecificationAttributeOption SET Name = @Name, ColorSquaresRgb = @Rgb, DisplayOrder = @DisplayOrder WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", opt.Id);
        cmd.Parameters.AddWithValue("@Name", opt.Name);
        cmd.Parameters.AddWithValue("@Rgb", (object?)opt.ColorSquaresRgb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayOrder", opt.DisplayOrder);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteOptionAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SpecificationAttributeOption WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Product spec mappings ----

    public async Task<IReadOnlyList<ProductSpecificationMapping>> GetProductSpecsAsync(int productId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, AttributeType, SpecificationAttributeOptionId, CustomValue, AllowFiltering, ShowOnProductPage, DisplayOrder FROM ProductSpecificationAttribute WHERE ProductId = @ProductId ORDER BY DisplayOrder";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProductSpecificationMapping>();
        while (await r.ReadAsync(ct)) list.Add(ReadProductSpec(r));
        return list;
    }

    public async Task<ProductSpecificationMapping?> GetProductSpecAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, AttributeType, SpecificationAttributeOptionId, CustomValue, AllowFiltering, ShowOnProductPage, DisplayOrder FROM ProductSpecificationAttribute WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadProductSpec(r) : null;
    }

    public async Task<int> InsertProductSpecAsync(ProductSpecificationMapping spec, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ProductSpecificationAttribute
                (ProductId, AttributeType, SpecificationAttributeOptionId, CustomValue, AllowFiltering, ShowOnProductPage, DisplayOrder)
            OUTPUT INSERTED.Id
            VALUES (@ProductId, @AttrType, @OptionId, @CustomValue, @AllowFiltering, @ShowOnProductPage, @DisplayOrder)
            """;
        AddProductSpecParams(cmd, spec);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateProductSpecAsync(ProductSpecificationMapping spec, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ProductSpecificationAttribute SET
                AttributeType = @AttrType, SpecificationAttributeOptionId = @OptionId, CustomValue = @CustomValue,
                AllowFiltering = @AllowFiltering, ShowOnProductPage = @ShowOnProductPage, DisplayOrder = @DisplayOrder
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", spec.Id);
        AddProductSpecParams(cmd, spec);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteProductSpecAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ProductSpecificationAttribute WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Tags ----

    public async Task<IReadOnlyList<ProductTag>> GetAllTagsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM ProductTag ORDER BY Name";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProductTag>();
        while (await r.ReadAsync(ct)) list.Add(new() { Id = r.GetInt32(0), Name = r.GetString(1) });
        return list;
    }

    public async Task<IReadOnlyList<ProductTag>> GetTagsByProductAsync(int productId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT t.Id, t.Name FROM ProductTag t INNER JOIN ProductProductTag pt ON pt.ProductTagId = t.Id WHERE pt.ProductId = @ProductId ORDER BY t.Name";
        cmd.Parameters.AddWithValue("@ProductId", productId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProductTag>();
        while (await r.ReadAsync(ct)) list.Add(new() { Id = r.GetInt32(0), Name = r.GetString(1) });
        return list;
    }

    public async Task<ProductTag?> GetTagAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM ProductTag WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new() { Id = r.GetInt32(0), Name = r.GetString(1) } : null;
    }

    public async Task<ProductTag?> GetTagByNameAsync(string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM ProductTag WHERE Name = @Name";
        cmd.Parameters.AddWithValue("@Name", name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new() { Id = r.GetInt32(0), Name = r.GetString(1) } : null;
    }

    public async Task<int> InsertTagAsync(ProductTag tag, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ProductTag (Name) OUTPUT INSERTED.Id VALUES (@Name)";
        cmd.Parameters.AddWithValue("@Name", tag.Name);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task DeleteTagAsync(int id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ProductTag WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetProductTagsAsync(int productId, int[] tagIds, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM ProductProductTag WHERE ProductId = @ProductId";
        deleteCmd.Parameters.AddWithValue("@ProductId", productId);
        await deleteCmd.ExecuteNonQueryAsync(ct);

        foreach (var tagId in tagIds)
        {
            await using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = "INSERT INTO ProductProductTag (ProductId, ProductTagId) VALUES (@ProductId, @TagId)";
            insertCmd.Parameters.AddWithValue("@ProductId", productId);
            insertCmd.Parameters.AddWithValue("@TagId", tagId);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ---- Helpers ----

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(options.Value.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static SpecificationAttributeDef ReadSpecAttr(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        SpecificationAttributeGroupId = r.IsDBNull(2) ? null : r.GetInt32(2),
        DisplayOrder = r.GetInt32(3)
    };

    private static SpecificationAttributeOption ReadOption(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        SpecificationAttributeId = r.GetInt32(1),
        Name = r.GetString(2),
        ColorSquaresRgb = r.IsDBNull(3) ? null : r.GetString(3),
        DisplayOrder = r.GetInt32(4)
    };

    private static ProductSpecificationMapping ReadProductSpec(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        ProductId = r.GetInt32(1),
        AttributeType = (SpecificationAttributeType)r.GetInt32(2),
        SpecificationAttributeOptionId = r.IsDBNull(3) ? null : r.GetInt32(3),
        CustomValue = r.IsDBNull(4) ? null : r.GetString(4),
        AllowFiltering = r.GetBoolean(5),
        ShowOnProductPage = r.GetBoolean(6),
        DisplayOrder = r.GetInt32(7)
    };

    private static void AddProductSpecParams(SqlCommand cmd, ProductSpecificationMapping spec)
    {
        cmd.Parameters.AddWithValue("@ProductId", spec.ProductId);
        cmd.Parameters.AddWithValue("@AttrType", (int)spec.AttributeType);
        cmd.Parameters.AddWithValue("@OptionId", (object?)spec.SpecificationAttributeOptionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomValue", (object?)spec.CustomValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AllowFiltering", spec.AllowFiltering);
        cmd.Parameters.AddWithValue("@ShowOnProductPage", spec.ShowOnProductPage);
        cmd.Parameters.AddWithValue("@DisplayOrder", spec.DisplayOrder);
    }
}
