using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Jobs;

namespace Nomori.Marketplace.Data.Jobs;

/// <summary>
/// The queries and deletes of the clean-up jobs. Cross-module reads live here on purpose (see <see cref="IMaintenanceStore"/>). The status numbers
/// are those stored by the owning modules: shop order 0 pending and 5 cancelled; payment 0 pending and 1 authorized; stock hold 0 active, 1 committed, 2 released.
/// </summary>
public sealed class SqlMaintenanceStore(IOptions<DatabaseOptions> options) : IMaintenanceStore
{
    public Task<IReadOnlyList<int>> ExpiredAwaitingOrderIdsAsync(DateTime createdBeforeUtc, int take, CancellationToken cancellationToken) =>
        IdsAsync("""
            SELECT TOP (@Take) o.Id
            FROM CustomerOrder o
            WHERE o.AwaitingPayment = 1
              AND o.CreatedOnUtc < @Before
              AND EXISTS (SELECT 1 FROM ShopOrder s WHERE s.OrderId = o.Id AND s.Status = 0)
            ORDER BY o.CreatedOnUtc, o.Id
            """, take, createdBeforeUtc, cancellationToken);

    public async Task<IReadOnlyList<int>> PaymentIdsForOrderAsync(int orderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id FROM PaymentTransaction WHERE ReferenceType = 'order' AND ReferenceId = @OrderId ORDER BY Id";
        cmd.Parameters.AddWithValue("@OrderId", orderId);
        return await ReadIdsAsync(cmd, cancellationToken);
    }

    public Task<IReadOnlyList<int>> StalePaymentIdsAsync(DateTime changedBeforeUtc, int take, CancellationToken cancellationToken) =>
        IdsAsync("""
            SELECT TOP (@Take) p.Id
            FROM PaymentTransaction p
            WHERE p.ReferenceType = 'order'
              AND p.Status IN (0, 1)
              AND p.UpdatedOnUtc < @Before
              -- Every shop order is cancelled: nothing is left to ship, so the payment will never be completed.
              AND EXISTS (SELECT 1 FROM ShopOrder s WHERE s.OrderId = p.ReferenceId)
              AND NOT EXISTS (SELECT 1 FROM ShopOrder s WHERE s.OrderId = p.ReferenceId AND s.Status <> 5)
            ORDER BY p.UpdatedOnUtc, p.Id
            """, take, changedBeforeUtc, cancellationToken);

    public async Task<int> CloseExpiredReservationsAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE StockReservation SET Status = 2, UpdatedOnUtc = @Now WHERE Status = 0 AND ExpiresOnUtc <= @Now";
        Add(cmd, "@Now", nowUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<int> DeleteClosedReservationsAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken) =>
        DeleteAsync("DELETE TOP (@Take) FROM StockReservation WHERE Status IN (1, 2) AND UpdatedOnUtc < @Before", take, beforeUtc, cancellationToken);

    public Task<int> DeleteStaleCartLinesAsync(DateTime untouchedSinceUtc, int take, CancellationToken cancellationToken) =>
        DeleteAsync("DELETE TOP (@Take) FROM CartItem WHERE UpdatedOnUtc < @Before", take, untouchedSinceUtc, cancellationToken);

    public Task<int> DeleteRunsAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken) =>
        DeleteAsync("DELETE TOP (@Take) FROM ScheduledTaskRun WHERE StartedUtc < @Before", take, beforeUtc, cancellationToken);

    private async Task<IReadOnlyList<int>> IdsAsync(string sql, int take, DateTime before, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Take", take);
        Add(cmd, "@Before", before);
        return await ReadIdsAsync(cmd, cancellationToken);
    }

    private static async Task<IReadOnlyList<int>> ReadIdsAsync(SqlCommand cmd, CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private async Task<int> DeleteAsync(string sql, int take, DateTime before, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Take", take);
        Add(cmd, "@Before", before);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
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
