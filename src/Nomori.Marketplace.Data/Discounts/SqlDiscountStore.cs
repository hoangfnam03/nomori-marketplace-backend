using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Discounts;

namespace Nomori.Marketplace.Data.Discounts;

public sealed class SqlDiscountStore(IOptions<DatabaseOptions> options) : IDiscountStore
{
    private const string Columns =
        "Id, VendorId, Name, Code, Type, Value, MaxDiscountAmount, StartsOnUtc, EndsOnUtc, MinSubtotal, MaxUses, MaxUsesPerCustomer, UsedCount, Enabled, CreatedOnUtc, UpdatedOnUtc";

    // 2601 and 2627: a unique index says the row already exists.
    private static bool IsDuplicate(SqlException ex) => ex.Number is 2601 or 2627;

    public async Task<IReadOnlyList<Discount>> GetListAsync(int? vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM Discount WHERE {(vendorId is null ? "VendorId IS NULL" : "VendorId = @VendorId")} ORDER BY Id DESC";
        if (vendorId is { } id) cmd.Parameters.AddWithValue("@VendorId", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<Discount>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(Read(reader));
        return list;
    }

    public async Task<int> CountAsync(int? vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM Discount WHERE {(vendorId is null ? "VendorId IS NULL" : "VendorId = @VendorId")}";
        if (vendorId is { } id) cmd.Parameters.AddWithValue("@VendorId", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public Task<Discount?> GetAsync(int id, CancellationToken cancellationToken) => QueryOneAsync("Id = @P", id, cancellationToken);

    public Task<Discount?> GetByCodeAsync(string code, CancellationToken cancellationToken) => QueryOneAsync("Code = @P", code, cancellationToken);

    public async Task<int> InsertAsync(Discount discount, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Discount (VendorId, Name, Code, Type, Value, MaxDiscountAmount, StartsOnUtc, EndsOnUtc, MinSubtotal, MaxUses, MaxUsesPerCustomer, UsedCount, Enabled, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@VendorId, @Name, @Code, @Type, @Value, @MaxDiscountAmount, @StartsOnUtc, @EndsOnUtc, @MinSubtotal, @MaxUses, @MaxUsesPerCustomer, 0, @Enabled, @Now, @Now)
            """;
        cmd.Parameters.AddWithValue("@VendorId", (object?)discount.VendorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Now", discount.CreatedOnUtc);
        AddValues(cmd, discount);
        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            return 0;
        }
    }

    public async Task<bool> UpdateAsync(Discount discount, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // The owner and the use count are not written here: the first never changes, the second belongs to redeeming.
        cmd.CommandText = """
            UPDATE Discount SET Name = @Name, Code = @Code, Type = @Type, Value = @Value, MaxDiscountAmount = @MaxDiscountAmount,
                StartsOnUtc = @StartsOnUtc, EndsOnUtc = @EndsOnUtc, MinSubtotal = @MinSubtotal, MaxUses = @MaxUses,
                MaxUsesPerCustomer = @MaxUsesPerCustomer, Enabled = @Enabled, UpdatedOnUtc = @Now
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", discount.Id);
        cmd.Parameters.AddWithValue("@Now", discount.UpdatedOnUtc);
        AddValues(cmd, discount);
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            return false;
        }
    }

    public async Task<bool> HasUsageAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM DiscountUsage WHERE DiscountId = @Id) THEN 1 ELSE 0 END";
        cmd.Parameters.AddWithValue("@Id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Discount WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountCustomerUsesAsync(int discountId, int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DiscountUsage WHERE DiscountId = @DiscountId AND CustomerId = @CustomerId";
        cmd.Parameters.AddWithValue("@DiscountId", discountId);
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<Discount>> GetOffersAsync(IReadOnlyCollection<int> vendorIds, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        var shops = vendorIds.Select((id, i) => (Name: $"@V{i}", Id: id)).ToList();
        var shopFilter = shops.Count == 0 ? "VendorId IS NULL" : $"(VendorId IS NULL OR VendorId IN ({string.Join(", ", shops.Select(s => s.Name))}))";
        // Same as DiscountRules.IsOffered: switched on, not over, the platform's or a shop's of the cart.
        cmd.CommandText = $"""
            SELECT TOP (@Max) {Columns} FROM Discount
            WHERE Enabled = 1 AND (EndsOnUtc IS NULL OR EndsOnUtc > @Now) AND {shopFilter}
            ORDER BY Id DESC
            """;
        cmd.Parameters.AddWithValue("@Max", DiscountLimits.MaxOffers);
        cmd.Parameters.AddWithValue("@Now", nowUtc);
        foreach (var (name, id) in shops) cmd.Parameters.AddWithValue(name, id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<Discount>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(Read(reader));
        return list;
    }

    public async Task<IReadOnlyDictionary<int, int>> CountCustomerUsesAsync(IReadOnlyCollection<int> discountIds, int customerId, CancellationToken cancellationToken)
    {
        var uses = new Dictionary<int, int>();
        if (discountIds.Count == 0) return uses;

        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        var ids = discountIds.Select((id, i) => (Name: $"@D{i}", Id: id)).ToList();
        cmd.CommandText = $"""
            SELECT DiscountId, COUNT(*) FROM DiscountUsage
            WHERE CustomerId = @CustomerId AND DiscountId IN ({string.Join(", ", ids.Select(d => d.Name))})
            GROUP BY DiscountId
            """;
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        foreach (var (name, id) in ids) cmd.Parameters.AddWithValue(name, id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) uses[reader.GetInt32(0)] = reader.GetInt32(1);
        return uses;
    }

    public async Task<RedeemOutcome> TryRedeemAsync(RedeemRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int usedCount;
        int? maxUses;
        int? maxPerCustomer;
        bool enabled;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            // The lock holds until the commit, so two buyers of the last use are served one after the other.
            cmd.CommandText = "SELECT UsedCount, MaxUses, MaxUsesPerCustomer, Enabled FROM Discount WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", request.DiscountId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return RedeemOutcome.Unavailable;
            (usedCount, maxUses, maxPerCustomer, enabled) = (
                reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetBoolean(3));
        }

        if (!enabled) return await RefuseAsync(transaction, RedeemOutcome.Unavailable, cancellationToken);
        if (maxUses is { } total && usedCount >= total) return await RefuseAsync(transaction, RedeemOutcome.LimitReached, cancellationToken);

        if (maxPerCustomer is { } perCustomer)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COUNT(*) FROM DiscountUsage WHERE DiscountId = @DiscountId AND CustomerId = @CustomerId";
            cmd.Parameters.AddWithValue("@DiscountId", request.DiscountId);
            cmd.Parameters.AddWithValue("@CustomerId", request.CustomerId);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= perCustomer)
                return await RefuseAsync(transaction, RedeemOutcome.CustomerLimitReached, cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO DiscountUsage (DiscountId, OrderId, CustomerId, Amount, CreatedOnUtc) VALUES (@DiscountId, @OrderId, @CustomerId, @Amount, @Now);
                UPDATE Discount SET UsedCount = UsedCount + 1 WHERE Id = @DiscountId;
                """;
            cmd.Parameters.AddWithValue("@DiscountId", request.DiscountId);
            cmd.Parameters.AddWithValue("@OrderId", request.OrderId);
            cmd.Parameters.AddWithValue("@CustomerId", request.CustomerId);
            cmd.Parameters.AddWithValue("@Amount", request.Amount);
            cmd.Parameters.AddWithValue("@Now", request.NowUtc);
            try
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex) when (IsDuplicate(ex))
            {
                // This order already used a code: it is not used twice.
                await transaction.RollbackAsync(cancellationToken);
                return RedeemOutcome.Unavailable;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return RedeemOutcome.Redeemed;
    }

    public async Task<bool> ReleaseAsync(int orderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int? discountId = null;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM DiscountUsage OUTPUT DELETED.DiscountId WHERE OrderId = @OrderId";
            cmd.Parameters.AddWithValue("@OrderId", orderId);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result is int id) discountId = id;
        }
        if (discountId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE Discount SET UsedCount = UsedCount - 1 WHERE Id = @Id AND UsedCount > 0";
            cmd.Parameters.AddWithValue("@Id", discountId.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<RedeemOutcome> RefuseAsync(SqlTransaction transaction, RedeemOutcome outcome, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return outcome;
    }

    private static void AddValues(SqlCommand cmd, Discount d)
    {
        cmd.Parameters.AddWithValue("@Name", d.Name);
        cmd.Parameters.AddWithValue("@Code", d.Code);
        cmd.Parameters.AddWithValue("@Type", (int)d.Type);
        cmd.Parameters.AddWithValue("@Value", d.Value);
        cmd.Parameters.AddWithValue("@MaxDiscountAmount", (object?)d.MaxDiscountAmount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StartsOnUtc", (object?)d.StartsOnUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@EndsOnUtc", (object?)d.EndsOnUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MinSubtotal", (object?)d.MinSubtotal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MaxUses", (object?)d.MaxUses ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MaxUsesPerCustomer", (object?)d.MaxUsesPerCustomer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Enabled", d.Enabled);
    }

    private async Task<Discount?> QueryOneAsync(string condition, object value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM Discount WHERE {condition}";
        cmd.Parameters.AddWithValue("@P", value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static Discount Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        VendorId = r.IsDBNull(1) ? null : r.GetInt32(1),
        Name = r.GetString(2),
        Code = r.GetString(3),
        Type = (DiscountType)r.GetInt32(4),
        Value = r.GetDecimal(5),
        MaxDiscountAmount = r.IsDBNull(6) ? null : r.GetDecimal(6),
        StartsOnUtc = r.IsDBNull(7) ? null : DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc),
        EndsOnUtc = r.IsDBNull(8) ? null : DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc),
        MinSubtotal = r.IsDBNull(9) ? null : r.GetDecimal(9),
        MaxUses = r.IsDBNull(10) ? null : r.GetInt32(10),
        MaxUsesPerCustomer = r.IsDBNull(11) ? null : r.GetInt32(11),
        UsedCount = r.GetInt32(12),
        Enabled = r.GetBoolean(13),
        CreatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(14), DateTimeKind.Utc),
        UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(15), DateTimeKind.Utc)
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
