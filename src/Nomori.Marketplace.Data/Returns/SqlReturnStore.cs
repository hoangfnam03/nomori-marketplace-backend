using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Returns;

namespace Nomori.Marketplace.Data.Returns;

public sealed class SqlReturnStore(IOptions<DatabaseOptions> options) : IReturnStore
{
    private const string Columns =
        "Id, Number, ShopOrderId, OrderId, ShopOrderNumber, VendorId, ShopName, CustomerId, Status, Reason, CustomerNote, ResolutionNote, " +
        "CurrencyCode, RefundAmount, Restocked, PaymentId, CreatedOnUtc, UpdatedOnUtc";

    public async Task<(ReturnRequest? Request, bool QuantityExceeded)> InsertAsync(ReturnRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Two requests for the same shop order queue here, so the quantity check below sees the first one's lines.
            await using (var lockCmd = Command(connection, transaction, "SELECT Id FROM ShopOrder WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id"))
            {
                lockCmd.Parameters.AddWithValue("@Id", request.ShopOrderId);
                await lockCmd.ExecuteScalarAsync(cancellationToken);
            }

            foreach (var line in request.Lines)
            {
                await using var check = Command(connection, transaction, """
                    SELECT l.Quantity - COALESCE((
                        SELECT SUM(rl.Quantity) FROM ReturnRequestLine rl
                        INNER JOIN ReturnRequest r ON r.Id = rl.ReturnRequestId
                        WHERE rl.OrderLineId = l.Id AND r.Status NOT IN (2, 5)), 0)
                    FROM OrderLine l WHERE l.Id = @LineId AND l.ShopOrderId = @ShopOrderId
                    """);
                check.Parameters.AddWithValue("@LineId", line.OrderLineId);
                check.Parameters.AddWithValue("@ShopOrderId", request.ShopOrderId);
                if (await check.ExecuteScalarAsync(cancellationToken) is not int free || line.Quantity > free)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (null, true);
                }
            }

            int id;
            await using (var cmd = Command(connection, transaction, """
                INSERT INTO ReturnRequest (Number, ShopOrderId, OrderId, ShopOrderNumber, VendorId, ShopName, CustomerId, Status, Reason, CustomerNote,
                    CurrencyCode, RefundAmount, Restocked, CreatedOnUtc, UpdatedOnUtc)
                OUTPUT INSERTED.Id
                VALUES (REPLACE(CONVERT(nvarchar(36), NEWID()), '-', ''), @ShopOrderId, @OrderId, @ShopOrderNumber, @VendorId, @ShopName, @CustomerId, @Status, @Reason,
                    @CustomerNote, @CurrencyCode, @RefundAmount, 0, @Created, @Created)
                """))
            {
                cmd.Parameters.AddWithValue("@ShopOrderId", request.ShopOrderId);
                cmd.Parameters.AddWithValue("@OrderId", request.OrderId);
                cmd.Parameters.AddWithValue("@ShopOrderNumber", request.ShopOrderNumber);
                cmd.Parameters.AddWithValue("@VendorId", request.VendorId);
                cmd.Parameters.AddWithValue("@ShopName", request.ShopName);
                cmd.Parameters.AddWithValue("@CustomerId", request.CustomerId);
                cmd.Parameters.AddWithValue("@Status", (int)request.Status);
                cmd.Parameters.AddWithValue("@Reason", request.Reason);
                cmd.Parameters.AddWithValue("@CustomerNote", (object?)request.CustomerNote ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CurrencyCode", request.CurrencyCode);
                cmd.Parameters.AddWithValue("@RefundAmount", request.RefundAmount);
                Add(cmd, "@Created", request.CreatedOnUtc);
                id = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            }

            await using (var number = Command(connection, transaction, "UPDATE ReturnRequest SET Number = @Number WHERE Id = @Id"))
            {
                number.Parameters.AddWithValue("@Number", ReturnRules.NumberFor(request.CreatedOnUtc, id));
                number.Parameters.AddWithValue("@Id", id);
                await number.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in request.Lines)
            {
                await using var cmd = Command(connection, transaction, """
                    INSERT INTO ReturnRequestLine (ReturnRequestId, OrderLineId, Name, VariantLabel, ProductId, CombinationId, Quantity, Amount)
                    VALUES (@RequestId, @OrderLineId, @Name, @VariantLabel, @ProductId, @CombinationId, @Quantity, @Amount)
                    """);
                cmd.Parameters.AddWithValue("@RequestId", id);
                cmd.Parameters.AddWithValue("@OrderLineId", line.OrderLineId);
                cmd.Parameters.AddWithValue("@Name", line.Name);
                cmd.Parameters.AddWithValue("@VariantLabel", (object?)line.VariantLabel ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                cmd.Parameters.AddWithValue("@CombinationId", (object?)line.CombinationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Quantity", line.Quantity);
                cmd.Parameters.AddWithValue("@Amount", line.Amount);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return (await GetAsync(id, cancellationToken), false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<int, int>> HeldQuantitiesAsync(int shopOrderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT rl.OrderLineId, SUM(rl.Quantity)
            FROM ReturnRequestLine rl INNER JOIN ReturnRequest r ON r.Id = rl.ReturnRequestId
            WHERE r.ShopOrderId = @ShopOrderId AND r.Status NOT IN (2, 5)
            GROUP BY rl.OrderLineId
            """;
        cmd.Parameters.AddWithValue("@ShopOrderId", shopOrderId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var held = new Dictionary<int, int>();
        while (await reader.ReadAsync(cancellationToken)) held[reader.GetInt32(0)] = reader.GetInt32(1);
        return held;
    }

    public async Task<ReturnRequest?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        ReturnRequest? request;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {Columns} FROM ReturnRequest WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            request = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        }
        if (request is null) return null;

        await using var lines = connection.CreateCommand();
        lines.CommandText = """
            SELECT Id, ReturnRequestId, OrderLineId, Name, VariantLabel, ProductId, CombinationId, Quantity, Amount
            FROM ReturnRequestLine WHERE ReturnRequestId = @Id ORDER BY Id
            """;
        lines.Parameters.AddWithValue("@Id", id);
        await using var lineReader = await lines.ExecuteReaderAsync(cancellationToken);
        while (await lineReader.ReadAsync(cancellationToken))
        {
            request.Lines.Add(new ReturnLine
            {
                Id = lineReader.GetInt32(0), ReturnRequestId = lineReader.GetInt32(1), OrderLineId = lineReader.GetInt32(2), Name = lineReader.GetString(3),
                VariantLabel = lineReader.IsDBNull(4) ? null : lineReader.GetString(4), ProductId = lineReader.GetInt32(5),
                CombinationId = lineReader.IsDBNull(6) ? null : lineReader.GetInt32(6), Quantity = lineReader.GetInt32(7), Amount = lineReader.GetDecimal(8)
            });
        }
        return request;
    }

    public async Task<PagedResult<ReturnRequest>> GetPagedAsync(ReturnQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string where = """
            WHERE (@CustomerId IS NULL OR CustomerId = @CustomerId)
              AND (@VendorId IS NULL OR VendorId = @VendorId)
              AND (@Status IS NULL OR Status = @Status)
            """;

        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.Int) { Value = (object?)query.CustomerId ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@VendorId", SqlDbType.Int) { Value = (object?)query.VendorId ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.Int) { Value = query.Status is null ? DBNull.Value : (int)query.Status });
        }

        int total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM ReturnRequest {where}";
            Bind(count);
            total = (int)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM ReturnRequest {where} ORDER BY Id DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        Bind(cmd);
        cmd.Parameters.AddWithValue("@Skip", (query.Page - 1) * query.PageSize);
        cmd.Parameters.AddWithValue("@Take", query.PageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<ReturnRequest>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return new PagedResult<ReturnRequest>(items, total, query.Page, query.PageSize);
    }

    public async Task<bool> TryTransitionAsync(ReturnTransition transition, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // One statement: the status changes only while it is still the expected one. Going back from refunded to received (a refund the
        // gateway refused) clears the payment again.
        cmd.CommandText = """
            UPDATE ReturnRequest
            SET Status = @To, UpdatedOnUtc = @Now,
                ResolutionNote = COALESCE(@Note, ResolutionNote),
                Restocked = COALESCE(@Restocked, Restocked),
                PaymentId = CASE WHEN @To = 4 THEN @PaymentId WHEN @To = 3 THEN NULL ELSE PaymentId END
            WHERE Id = @Id AND Status = @From
            """;
        cmd.Parameters.AddWithValue("@Id", transition.Id);
        cmd.Parameters.AddWithValue("@From", (int)transition.From);
        cmd.Parameters.AddWithValue("@To", (int)transition.To);
        cmd.Parameters.Add(new SqlParameter("@Note", SqlDbType.NVarChar, 500) { Value = (object?)transition.ResolutionNote ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@Restocked", SqlDbType.Bit) { Value = (object?)transition.Restocked ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@PaymentId", SqlDbType.Int) { Value = (object?)transition.PaymentId ?? DBNull.Value });
        Add(cmd, "@Now", transition.NowUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<int>> PaymentIdsForOrderAsync(int orderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // Paid (2) or partly refunded (3).
        cmd.CommandText = "SELECT Id FROM PaymentTransaction WHERE ReferenceType = 'order' AND ReferenceId = @OrderId AND Status IN (2, 3) ORDER BY Id DESC";
        cmd.Parameters.AddWithValue("@OrderId", orderId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static ReturnRequest Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0), Number = r.GetString(1), ShopOrderId = r.GetInt32(2), OrderId = r.GetInt32(3), ShopOrderNumber = r.GetString(4),
        VendorId = r.GetInt32(5), ShopName = r.GetString(6), CustomerId = r.GetInt32(7), Status = (ReturnStatus)r.GetInt32(8), Reason = r.GetString(9),
        CustomerNote = r.IsDBNull(10) ? null : r.GetString(10), ResolutionNote = r.IsDBNull(11) ? null : r.GetString(11), CurrencyCode = r.GetString(12),
        RefundAmount = r.GetDecimal(13), Restocked = r.GetBoolean(14), PaymentId = r.IsDBNull(15) ? null : r.GetInt32(15),
        CreatedOnUtc = Utc(r.GetDateTime(16)), UpdatedOnUtc = Utc(r.GetDateTime(17))
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Add(SqlCommand cmd, string name, DateTime value) =>
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime2) { Value = value });

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
