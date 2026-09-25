using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Data.Vendors;

public sealed class SqlVendorStore(IOptions<DatabaseOptions> options) : IVendorStore
{
    private const string SelectColumns =
        "Id, Name, Email, Description, PictureId, AddressId, AdminComment, Active, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc";

    public async Task<Vendor?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Vendor WHERE Id = @Id AND Deleted = 0";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVendor(reader) : null;
    }

    public async Task<Vendor?> GetByCustomerIdAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT v.Id, v.Name, v.Email, v.Description, v.PictureId, v.AddressId,
                   v.AdminComment, v.Active, v.Deleted, v.DisplayOrder, v.CreatedOnUtc, v.UpdatedOnUtc
            FROM Vendor v
            INNER JOIN Customer c ON c.VendorId = v.Id
            WHERE c.Id = @CustomerId AND v.Deleted = 0
            """;
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVendor(reader) : null;
    }

    public async Task<(IReadOnlyList<Vendor> Items, int TotalCount)> GetPagedAsync(VendorQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var where = BuildWhere(query);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM Vendor WHERE {where}";
        AddFilterParams(countCmd, query);
        var totalCount = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var cmd = connection.CreateCommand();
        var offset = (query.Page - 1) * query.PageSize;
        cmd.CommandText = $"SELECT {SelectColumns} FROM Vendor WHERE {where} ORDER BY DisplayOrder, Name OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
        AddFilterParams(cmd, query);
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@PageSize", query.PageSize);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Vendor>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadVendor(reader));
        return (items, totalCount);
    }

    public async Task<int> InsertAsync(Vendor vendor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Vendor (Name, Email, Description, PictureId, AddressId, AdminComment, Active, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Name, @Email, @Description, @PictureId, @AddressId, @AdminComment, @Active, 0, @DisplayOrder, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        AddWriteParams(cmd, vendor);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateAsync(Vendor vendor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Vendor SET
                Name = @Name, Email = @Email, Description = @Description,
                PictureId = @PictureId, AddressId = @AddressId, AdminComment = @AdminComment,
                Active = @Active, DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND Deleted = 0
            """;
        cmd.Parameters.AddWithValue("@Id", vendor.Id);
        AddWriteParams(cmd, vendor);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Vendor SET Deleted = 1 WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetCustomerVendorAsync(int customerId, int? vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Customer SET VendorId = @VendorId WHERE Id = @CustomerId";
        cmd.Parameters.AddWithValue("@VendorId", (object?)vendorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- Notes ----

    public async Task<(IReadOnlyList<VendorNote> Items, int TotalCount)> GetNotesPagedAsync(
        int vendorId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM VendorNote WHERE VendorId = @VendorId";
        countCmd.Parameters.AddWithValue("@VendorId", vendorId);
        var totalCount = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var cmd = connection.CreateCommand();
        var offset = (page - 1) * pageSize;
        cmd.CommandText = "SELECT Id, VendorId, Note, CreatedOnUtc FROM VendorNote WHERE VendorId = @VendorId ORDER BY CreatedOnUtc DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@PageSize", pageSize);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<VendorNote>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new VendorNote { Id = reader.GetInt32(0), VendorId = reader.GetInt32(1), Note = reader.GetString(2), CreatedOnUtc = reader.GetDateTime(3) });
        return (items, totalCount);
    }

    public async Task<int> InsertNoteAsync(VendorNote note, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO VendorNote (VendorId, Note, CreatedOnUtc) OUTPUT INSERTED.Id VALUES (@VendorId, @Note, @CreatedOnUtc)";
        cmd.Parameters.AddWithValue("@VendorId", note.VendorId);
        cmd.Parameters.AddWithValue("@Note", note.Note);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", note.CreatedOnUtc);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> DeleteNoteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM VendorNote WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string BuildWhere(VendorQuery q)
    {
        var parts = new List<string> { "Deleted = 0" };
        if (!string.IsNullOrWhiteSpace(q.Search)) parts.Add("(Name LIKE @Search OR Email LIKE @Search)");
        if (q.Active.HasValue) parts.Add("Active = @Active");
        return string.Join(" AND ", parts);
    }

    private static void AddFilterParams(SqlCommand cmd, VendorQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Search)) cmd.Parameters.AddWithValue("@Search", $"%{q.Search}%");
        if (q.Active.HasValue) cmd.Parameters.AddWithValue("@Active", q.Active.Value);
    }

    private static void AddWriteParams(SqlCommand cmd, Vendor v)
    {
        cmd.Parameters.AddWithValue("@Name", v.Name);
        cmd.Parameters.AddWithValue("@Email", v.Email);
        cmd.Parameters.AddWithValue("@Description", (object?)v.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PictureId", v.PictureId);
        cmd.Parameters.AddWithValue("@AddressId", v.AddressId);
        cmd.Parameters.AddWithValue("@AdminComment", (object?)v.AdminComment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Active", v.Active);
        cmd.Parameters.AddWithValue("@DisplayOrder", v.DisplayOrder);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", v.CreatedOnUtc);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", v.UpdatedOnUtc);
    }

    private static Vendor ReadVendor(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        Email = r.GetString(2),
        Description = r.IsDBNull(3) ? null : r.GetString(3),
        PictureId = r.GetInt32(4),
        AddressId = r.GetInt32(5),
        AdminComment = r.IsDBNull(6) ? null : r.GetString(6),
        Active = r.GetBoolean(7),
        Deleted = r.GetBoolean(8),
        DisplayOrder = r.GetInt32(9),
        CreatedOnUtc = r.GetDateTime(10),
        UpdatedOnUtc = r.GetDateTime(11)
    };
}
