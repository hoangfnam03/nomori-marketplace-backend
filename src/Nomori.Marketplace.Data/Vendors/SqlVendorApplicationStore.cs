using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Data.Vendors;

public sealed class SqlVendorApplicationStore(IOptions<DatabaseOptions> options) : IVendorApplicationStore
{
    private const string SelectColumns = """
        a.Id, a.CustomerId, a.ShopName, a.Email, a.PhoneNumber, a.Description, a.TaxCode, a.BusinessAddress,
        a.Status, a.RejectReason, a.ReviewedByCustomerId, a.ReviewedOnUtc, a.VendorId, a.CreatedOnUtc, a.UpdatedOnUtc,
        c.Email AS CustomerEmail, c.Username AS CustomerUsername
        """;

    private const string FromClause = "FROM VendorApplication a INNER JOIN Customer c ON c.Id = a.CustomerId";

    public async Task<VendorApplication?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} {FromClause} WHERE a.Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<(IReadOnlyList<VendorApplication> Items, int TotalCount)> GetPagedAsync(
        VendorApplicationQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var where = BuildWhere(query);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) {FromClause} WHERE {where}";
        AddFilterParams(countCmd, query);
        var totalCount = (int)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var cmd = connection.CreateCommand();
        var direction = query.OldestFirst ? "ASC" : "DESC";
        cmd.CommandText = $"SELECT {SelectColumns} {FromClause} WHERE {where} ORDER BY a.CreatedOnUtc {direction}, a.Id {direction} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
        AddFilterParams(cmd, query);
        cmd.Parameters.AddWithValue("@Offset", (query.Page - 1) * query.PageSize);
        cmd.Parameters.AddWithValue("@PageSize", query.PageSize);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<VendorApplication>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return (items, totalCount);
    }

    public async Task<bool> ShopNameExistsAsync(string shopName, int? excludeApplicationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM Vendor WHERE Deleted = 0 AND Name = @Name)
                          OR EXISTS (SELECT 1 FROM VendorApplication WHERE Status = 0 AND ShopName = @Name
                                     AND (@ExcludeId IS NULL OR Id <> @ExcludeId))
                        THEN 1 ELSE 0 END
            """;
        cmd.Parameters.AddWithValue("@Name", shopName);
        cmd.Parameters.AddWithValue("@ExcludeId", (object?)excludeApplicationId ?? DBNull.Value);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    public async Task<int?> InsertAsync(VendorApplication application, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VendorApplication
                (CustomerId, ShopName, Email, PhoneNumber, Description, TaxCode, BusinessAddress, Status, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@CustomerId, @ShopName, @Email, @PhoneNumber, @Description, @TaxCode, @BusinessAddress, 0, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        cmd.Parameters.AddWithValue("@CustomerId", application.CustomerId);
        AddContentParams(cmd, application);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", application.CreatedOnUtc);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", application.UpdatedOnUtc);
        try
        {
            return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // UX_VendorApplication_Customer_Pending: a pending application already exists.
            return null;
        }
    }

    public async Task<bool> UpdateContentAsync(VendorApplication application, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE VendorApplication SET
                ShopName = @ShopName, Email = @Email, PhoneNumber = @PhoneNumber, Description = @Description,
                TaxCode = @TaxCode, BusinessAddress = @BusinessAddress, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND Status = 0
            """;
        cmd.Parameters.AddWithValue("@Id", application.Id);
        AddContentParams(cmd, application);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", application.UpdatedOnUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> CloseAsync(
        int id, VendorApplicationStatus status, string? rejectReason, int? reviewedByCustomerId,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE VendorApplication SET
                Status = @Status, RejectReason = @RejectReason, ReviewedByCustomerId = @ReviewedBy,
                ReviewedOnUtc = @ReviewedOnUtc, UpdatedOnUtc = @NowUtc
            WHERE Id = @Id AND Status = 0
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Status", (int)status);
        cmd.Parameters.AddWithValue("@RejectReason", (object?)rejectReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReviewedBy", (object?)reviewedByCustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReviewedOnUtc", reviewedByCustomerId is null ? DBNull.Value : nowUtc);
        cmd.Parameters.AddWithValue("@NowUtc", nowUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ApproveApplicationResult> ApproveAsync(
        int id, string shopName, string? adminComment, int reviewedByCustomerId,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int customerId;
        string email;
        await using (var lockCmd = connection.CreateCommand())
        {
            lockCmd.Transaction = transaction;
            lockCmd.CommandText = "SELECT CustomerId, Email FROM VendorApplication WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id AND Status = 0";
            lockCmd.Parameters.AddWithValue("@Id", id);
            await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return new ApproveApplicationResult(ApproveApplicationOutcome.NotPending);
            }

            customerId = reader.GetInt32(0);
            email = reader.GetString(1);
        }

        await using (var linkedCmd = connection.CreateCommand())
        {
            linkedCmd.Transaction = transaction;
            linkedCmd.CommandText = "SELECT VendorId FROM Customer WITH (UPDLOCK, ROWLOCK) WHERE Id = @CustomerId";
            linkedCmd.Parameters.AddWithValue("@CustomerId", customerId);
            var linked = await linkedCmd.ExecuteScalarAsync(cancellationToken);
            if (linked is not null and not DBNull)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ApproveApplicationResult(ApproveApplicationOutcome.ApplicantAlreadyVendor);
            }
        }

        int vendorId;
        await using (var vendorCmd = connection.CreateCommand())
        {
            vendorCmd.Transaction = transaction;
            vendorCmd.CommandText = """
                INSERT INTO Vendor (Name, Email, Description, PhoneNumber, TaxCode, BusinessAddress, PictureId, AddressId, AdminComment, Active, Deleted, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
                OUTPUT INSERTED.Id
                SELECT @ShopName, @Email, a.Description, a.PhoneNumber, a.TaxCode, a.BusinessAddress, 0, 0, @AdminComment, 1, 0, 0, @NowUtc, @NowUtc
                FROM VendorApplication a WHERE a.Id = @Id
                """;
            vendorCmd.Parameters.AddWithValue("@Id", id);
            vendorCmd.Parameters.AddWithValue("@ShopName", shopName);
            vendorCmd.Parameters.AddWithValue("@Email", email);
            vendorCmd.Parameters.AddWithValue("@AdminComment", (object?)adminComment ?? DBNull.Value);
            vendorCmd.Parameters.AddWithValue("@NowUtc", nowUtc);
            vendorId = (int)(await vendorCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var memberCmd = connection.CreateCommand())
        {
            memberCmd.Transaction = transaction;
            memberCmd.CommandText = """
                UPDATE Customer SET VendorId = @VendorId WHERE Id = @CustomerId;

                INSERT INTO CustomerCustomerRoleMapping (CustomerId, CustomerRoleId)
                SELECT @CustomerId, r.Id FROM CustomerRole r
                WHERE r.SystemName = 'Vendors'
                  AND NOT EXISTS (SELECT 1 FROM CustomerCustomerRoleMapping m WHERE m.CustomerId = @CustomerId AND m.CustomerRoleId = r.Id);

                UPDATE VendorApplication SET
                    Status = 1, VendorId = @VendorId, ReviewedByCustomerId = @ReviewedBy,
                    ReviewedOnUtc = @NowUtc, UpdatedOnUtc = @NowUtc
                WHERE Id = @Id;
                """;
            memberCmd.Parameters.AddWithValue("@Id", id);
            memberCmd.Parameters.AddWithValue("@VendorId", vendorId);
            memberCmd.Parameters.AddWithValue("@CustomerId", customerId);
            memberCmd.Parameters.AddWithValue("@ReviewedBy", reviewedByCustomerId);
            memberCmd.Parameters.AddWithValue("@NowUtc", nowUtc);
            await memberCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ApproveApplicationResult(ApproveApplicationOutcome.Approved, vendorId);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string BuildWhere(VendorApplicationQuery q)
    {
        var parts = new List<string> { "1 = 1" };
        if (q.Status.HasValue) parts.Add("a.Status = @Status");
        if (q.CustomerId.HasValue) parts.Add("a.CustomerId = @CustomerId");
        if (!string.IsNullOrWhiteSpace(q.Search))
            parts.Add("(a.ShopName LIKE @Search OR a.Email LIKE @Search OR c.Email LIKE @Search)");
        return string.Join(" AND ", parts);
    }

    private static void AddFilterParams(SqlCommand cmd, VendorApplicationQuery q)
    {
        if (q.Status.HasValue) cmd.Parameters.AddWithValue("@Status", (int)q.Status.Value);
        if (q.CustomerId.HasValue) cmd.Parameters.AddWithValue("@CustomerId", q.CustomerId.Value);
        if (!string.IsNullOrWhiteSpace(q.Search))
            cmd.Parameters.AddWithValue("@Search", $"%{EscapeLike(q.Search.Trim())}%");
    }

    private static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    private static void AddContentParams(SqlCommand cmd, VendorApplication a)
    {
        cmd.Parameters.AddWithValue("@ShopName", a.ShopName);
        cmd.Parameters.AddWithValue("@Email", a.Email);
        cmd.Parameters.AddWithValue("@PhoneNumber", a.PhoneNumber);
        cmd.Parameters.AddWithValue("@Description", (object?)a.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TaxCode", (object?)a.TaxCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@BusinessAddress", (object?)a.BusinessAddress ?? DBNull.Value);
    }

    private static VendorApplication Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        CustomerId = r.GetInt32(1),
        ShopName = r.GetString(2),
        Email = r.GetString(3),
        PhoneNumber = r.GetString(4),
        Description = r.IsDBNull(5) ? null : r.GetString(5),
        TaxCode = r.IsDBNull(6) ? null : r.GetString(6),
        BusinessAddress = r.IsDBNull(7) ? null : r.GetString(7),
        Status = (VendorApplicationStatus)r.GetInt32(8),
        RejectReason = r.IsDBNull(9) ? null : r.GetString(9),
        ReviewedByCustomerId = r.IsDBNull(10) ? null : r.GetInt32(10),
        ReviewedOnUtc = r.IsDBNull(11) ? null : r.GetDateTime(11),
        VendorId = r.IsDBNull(12) ? null : r.GetInt32(12),
        CreatedOnUtc = r.GetDateTime(13),
        UpdatedOnUtc = r.GetDateTime(14),
        CustomerEmail = r.GetString(15),
        CustomerUsername = r.IsDBNull(16) ? null : r.GetString(16)
    };
}
