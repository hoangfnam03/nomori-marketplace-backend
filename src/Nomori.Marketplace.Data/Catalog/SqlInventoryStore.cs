using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

/// <summary>
/// Stock changes that must not race. Every write runs in one transaction that first locks the stock row
/// (<c>UPDLOCK, HOLDLOCK</c>), reads what is reserved, asks <see cref="StockRules"/>, and only then writes.
/// </summary>
public sealed class SqlInventoryStore(IOptions<DatabaseOptions> options) : IInventoryStore
{
    private const int Active = 0;
    private const int Committed = 1;
    private const int Released = 2;

    public async Task<StockLevel?> GetLevelAsync(int productId, int? combinationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var onHand = combinationId is { } cid
            ? await ScalarAsync(connection, null, "SELECT StockQuantity FROM ProductAttributeCombination WHERE Id = @C AND ProductId = @P", cancellationToken, ("@C", cid), ("@P", productId))
            : await ScalarAsync(connection, null, "SELECT StockQuantity FROM Product WHERE Id = @P AND Deleted = 0", cancellationToken, ("@P", productId));
        if (onHand is null) return null;

        var reserved = await ReservedAsync(connection, null, productId, combinationId, null, nowUtc, cancellationToken);
        return new StockLevel(onHand.Value, reserved);
    }

    public async Task<IReadOnlyDictionary<int, StockLevel>> GetCombinationLevelsAsync(int productId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.Id, c.StockQuantity,
                COALESCE((SELECT SUM(r.Quantity) FROM StockReservation r
                          WHERE r.ProductId = c.ProductId AND r.CombinationId = c.Id AND r.Status = 0 AND r.ExpiresOnUtc > @Now), 0)
            FROM ProductAttributeCombination c WHERE c.ProductId = @P
            """;
        cmd.Parameters.AddWithValue("@P", productId);
        cmd.Parameters.AddWithValue("@Now", nowUtc);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<int, StockLevel>();
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetInt32(0)] = new StockLevel(reader.GetInt32(1), reader.GetInt32(2));
        return result;
    }

    public async Task<InventoryChange> AdjustAsync(StockAdjustment adjustment, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var onHand = await LockAsync(connection, transaction, adjustment.ProductId, adjustment.CombinationId, cancellationToken);
        if (onHand is null) return new InventoryChange(InventoryOutcome.NotFound);

        var reserved = await ReservedAsync(connection, transaction, adjustment.ProductId, adjustment.CombinationId, null, nowUtc, cancellationToken);
        if (!StockRules.CanAdjust(onHand.Value, reserved, adjustment.Delta)) return new InventoryChange(InventoryOutcome.Insufficient, onHand.Value);

        await ApplyDeltaAsync(connection, transaction, adjustment.ProductId, adjustment.CombinationId, adjustment.Delta, nowUtc, cancellationToken);
        var after = onHand.Value + adjustment.Delta;
        await InsertMovementAsync(connection, transaction, new StockMovement
        {
            ProductId = adjustment.ProductId, CombinationId = adjustment.CombinationId, Delta = adjustment.Delta, QuantityAfter = after,
            Reason = adjustment.Reason, Reference = adjustment.Reference, Note = adjustment.Note, ActorCustomerId = adjustment.ActorCustomerId, CreatedOnUtc = nowUtc
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new InventoryChange(InventoryOutcome.Ok, after);
    }

    public async Task RecordMovementAsync(StockMovement movement, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await InsertMovementAsync(connection, null, movement, cancellationToken);
    }

    public async Task<InventoryChange> ReserveAsync(StockReservationRequest request, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var onHand = await LockAsync(connection, transaction, request.ProductId, request.CombinationId, cancellationToken);
        if (onHand is null) return new InventoryChange(InventoryOutcome.NotFound);

        // What other references hold; this reference's own hold is being replaced.
        var reservedByOthers = await ReservedAsync(connection, transaction, request.ProductId, request.CombinationId, request.Reference, nowUtc, cancellationToken);
        if (!StockRules.CanReserve(onHand.Value, reservedByOthers, request.Quantity)) return new InventoryChange(InventoryOutcome.Insufficient, onHand.Value);

        var updated = await NonQueryAsync(connection, transaction, """
            UPDATE StockReservation SET Quantity = @Q, ExpiresOnUtc = @Expires, UpdatedOnUtc = @Now
            WHERE Reference = @R AND ProductId = @P AND Status = 0 AND ((CombinationId IS NULL AND @C IS NULL) OR CombinationId = @C)
            """, cancellationToken,
            ("@Q", request.Quantity), ("@Expires", request.ExpiresOnUtc), ("@Now", nowUtc), ("@R", request.Reference), ("@P", request.ProductId), ("@C", request.CombinationId));
        if (updated == 0)
        {
            await NonQueryAsync(connection, transaction, """
                INSERT INTO StockReservation (ProductId, CombinationId, Quantity, Reference, Status, ExpiresOnUtc, CreatedOnUtc, UpdatedOnUtc)
                VALUES (@P, @C, @Q, @R, 0, @Expires, @Now, @Now)
                """, cancellationToken,
                ("@P", request.ProductId), ("@C", request.CombinationId), ("@Q", request.Quantity), ("@R", request.Reference), ("@Expires", request.ExpiresOnUtc), ("@Now", nowUtc));
        }

        await transaction.CommitAsync(cancellationToken);
        return new InventoryChange(InventoryOutcome.Ok, onHand.Value);
    }

    public async Task<int> ReleaseAsync(string reference, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await NonQueryAsync(connection, null,
            "UPDATE StockReservation SET Status = @Released, UpdatedOnUtc = @Now WHERE Reference = @R AND Status = 0",
            cancellationToken, ("@Released", Released), ("@Now", nowUtc), ("@R", reference));
    }

    public async Task<InventoryChange> CommitAsync(string reference, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var holds = new List<(int Id, int ProductId, int? CombinationId, int Quantity, DateTime ExpiresOnUtc)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            // Ordered so that two commits lock stock rows in the same order.
            cmd.CommandText = """
                SELECT Id, ProductId, CombinationId, Quantity, ExpiresOnUtc FROM StockReservation WITH (UPDLOCK)
                WHERE Reference = @R AND Status = 0 ORDER BY ProductId, CombinationId, Id
                """;
            cmd.Parameters.AddWithValue("@R", reference);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                holds.Add((reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetInt32(3), reader.GetDateTime(4)));
        }

        // Unknown reference, or a hold that ran out: nothing is sold.
        if (holds.Count == 0 || holds.Any(h => h.ExpiresOnUtc <= nowUtc)) return new InventoryChange(InventoryOutcome.Expired);

        foreach (var hold in holds)
        {
            var onHand = await LockAsync(connection, transaction, hold.ProductId, hold.CombinationId, cancellationToken);
            if (onHand is null) return new InventoryChange(InventoryOutcome.NotFound);
            if (onHand.Value < hold.Quantity) return new InventoryChange(InventoryOutcome.Insufficient, onHand.Value);

            await ApplyDeltaAsync(connection, transaction, hold.ProductId, hold.CombinationId, -hold.Quantity, nowUtc, cancellationToken);
            await InsertMovementAsync(connection, transaction, new StockMovement
            {
                ProductId = hold.ProductId, CombinationId = hold.CombinationId, Delta = -hold.Quantity, QuantityAfter = onHand.Value - hold.Quantity,
                Reason = StockReasons.Sale, Reference = reference, CreatedOnUtc = nowUtc
            }, cancellationToken);
            await NonQueryAsync(connection, transaction, "UPDATE StockReservation SET Status = @Committed, UpdatedOnUtc = @Now WHERE Id = @Id",
                cancellationToken, ("@Committed", Committed), ("@Now", nowUtc), ("@Id", hold.Id));
        }

        await transaction.CommitAsync(cancellationToken);
        return new InventoryChange(InventoryOutcome.Ok);
    }

    public async Task<bool> HasActiveReservationsAsync(int productId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarAsync(connection, null,
            "SELECT TOP 1 1 FROM StockReservation WHERE ProductId = @P AND Status = 0 AND ExpiresOnUtc > @Now",
            cancellationToken, ("@P", productId), ("@Now", nowUtc)) is not null;
    }

    public async Task<(IReadOnlyList<StockMovement> Items, int TotalCount)> GetMovementsAsync(int productId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var total = await ScalarAsync(connection, null, "SELECT COUNT(*) FROM StockMovement WHERE ProductId = @P", cancellationToken, ("@P", productId)) ?? 0;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ProductId, CombinationId, Delta, QuantityAfter, Reason, Reference, Note, ActorCustomerId, CreatedOnUtc
            FROM StockMovement WHERE ProductId = @P ORDER BY Id DESC OFFSET @Offset ROWS FETCH NEXT @Size ROWS ONLY
            """;
        cmd.Parameters.AddWithValue("@P", productId);
        cmd.Parameters.AddWithValue("@Offset", (page - 1) * pageSize);
        cmd.Parameters.AddWithValue("@Size", pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<StockMovement>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new StockMovement
            {
                Id = reader.GetInt32(0), ProductId = reader.GetInt32(1), CombinationId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Delta = reader.GetInt32(3), QuantityAfter = reader.GetInt32(4), Reason = reader.GetString(5),
                Reference = reader.IsDBNull(6) ? null : reader.GetString(6), Note = reader.IsDBNull(7) ? null : reader.GetString(7),
                ActorCustomerId = reader.IsDBNull(8) ? null : reader.GetInt32(8), CreatedOnUtc = reader.GetDateTime(9)
            });
        }
        return (items, total);
    }

    public async Task SetSettingsAsync(int productId, bool trackInventory, int lowStockThreshold, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await NonQueryAsync(connection, null,
            "UPDATE Product SET TrackInventory = @Track, LowStockThreshold = @Threshold WHERE Id = @P AND Deleted = 0",
            cancellationToken, ("@Track", trackInventory), ("@Threshold", lowStockThreshold), ("@P", productId));
    }

    // ---- Helpers ----

    /// <summary>Locks the stock row (the combination when given, otherwise the product) and returns its quantity; null when it does not exist.</summary>
    private static Task<int?> LockAsync(SqlConnection connection, SqlTransaction transaction, int productId, int? combinationId, CancellationToken cancellationToken) =>
        combinationId is { } cid
            ? ScalarAsync(connection, transaction, "SELECT StockQuantity FROM ProductAttributeCombination WITH (UPDLOCK, HOLDLOCK) WHERE Id = @C AND ProductId = @P",
                cancellationToken, ("@C", cid), ("@P", productId))
            : ScalarAsync(connection, transaction, "SELECT StockQuantity FROM Product WITH (UPDLOCK, HOLDLOCK) WHERE Id = @P AND Deleted = 0",
                cancellationToken, ("@P", productId));

    /// <summary>Quantity held by active, unexpired reservations of a product (all combinations) or one combination, optionally leaving one reference out.</summary>
    private static async Task<int> ReservedAsync(
        SqlConnection connection, SqlTransaction? transaction, int productId, int? combinationId, string? excludeReference, DateTime nowUtc, CancellationToken cancellationToken) =>
        await ScalarAsync(connection, transaction, """
            SELECT COALESCE(SUM(Quantity), 0) FROM StockReservation
            WHERE ProductId = @P AND Status = 0 AND ExpiresOnUtc > @Now
              AND (@C IS NULL OR CombinationId = @C) AND (@Exclude IS NULL OR Reference <> @Exclude)
            """, cancellationToken, ("@P", productId), ("@Now", nowUtc), ("@C", combinationId), ("@Exclude", excludeReference)) ?? 0;

    /// <summary>Changes the stock of a combination and the product total together, or of a plain product.</summary>
    private static async Task ApplyDeltaAsync(
        SqlConnection connection, SqlTransaction transaction, int productId, int? combinationId, int delta, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (combinationId is { } cid)
        {
            await NonQueryAsync(connection, transaction, "UPDATE ProductAttributeCombination SET StockQuantity = StockQuantity + @D WHERE Id = @C",
                cancellationToken, ("@D", delta), ("@C", cid));
        }
        await NonQueryAsync(connection, transaction, "UPDATE Product SET StockQuantity = StockQuantity + @D, UpdatedOnUtc = @Now WHERE Id = @P",
            cancellationToken, ("@D", delta), ("@Now", nowUtc), ("@P", productId));
    }

    private static async Task InsertMovementAsync(SqlConnection connection, SqlTransaction? transaction, StockMovement m, CancellationToken cancellationToken) =>
        await NonQueryAsync(connection, transaction, """
            INSERT INTO StockMovement (ProductId, CombinationId, Delta, QuantityAfter, Reason, Reference, Note, ActorCustomerId, CreatedOnUtc)
            VALUES (@P, @C, @Delta, @After, @Reason, @Reference, @Note, @Actor, @Now)
            """, cancellationToken,
            ("@P", m.ProductId), ("@C", m.CombinationId), ("@Delta", m.Delta), ("@After", m.QuantityAfter), ("@Reason", m.Reason),
            ("@Reference", m.Reference), ("@Note", m.Note), ("@Actor", m.ActorCustomerId), ("@Now", m.CreatedOnUtc));

    private static async Task<int?> ScalarAsync(
        SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(connection, transaction, sql, parameters);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> NonQueryAsync(
        SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(connection, transaction, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
