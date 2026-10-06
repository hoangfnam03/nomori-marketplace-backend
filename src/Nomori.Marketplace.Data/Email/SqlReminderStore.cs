using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Email;

namespace Nomori.Marketplace.Data.Email;

/// <summary>
/// Reads orders and carts of other modules on purpose, in one place (like <c>SqlMaintenanceStore</c>). Shop order status 0 is pending.
/// </summary>
public sealed class SqlReminderStore(IOptions<DatabaseOptions> options) : IReminderStore
{
    public async Task<IReadOnlyList<UnpaidOrderCandidate>> UnpaidOrdersAsync(DateTime createdBeforeUtc, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@Take) o.Id, o.Number, o.CustomerId, o.Total, o.CurrencyCode
            FROM CustomerOrder o
            WHERE o.AwaitingPayment = 1
              AND o.CreatedOnUtc < @Before
              AND EXISTS (SELECT 1 FROM ShopOrder s WHERE s.OrderId = o.Id AND s.Status = 0)
              AND NOT EXISTS (SELECT 1 FROM ReminderLog r WHERE r.Kind = @Kind AND r.ReferenceId = o.Id)
            ORDER BY o.CreatedOnUtc, o.Id
            """;
        cmd.Parameters.AddWithValue("@Take", take);
        cmd.Parameters.AddWithValue("@Kind", ReminderKinds.UnpaidOrder);
        Add(cmd, "@Before", createdBeforeUtc);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<UnpaidOrderCandidate>();
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new UnpaidOrderCandidate(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetDecimal(3), reader.GetString(4)));
        return list;
    }

    public async Task<IReadOnlyList<AbandonedCartCandidate>> AbandonedCartsAsync(
        DateTime idleBeforeUtc, DateTime idleAfterUtc, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@Take) c.CustomerId, c.ItemCount, c.LastTouched
            FROM (
                SELECT CustomerId, COUNT(*) AS ItemCount, MAX(UpdatedOnUtc) AS LastTouched
                FROM CartItem GROUP BY CustomerId
            ) c
            WHERE c.LastTouched < @IdleBefore AND c.LastTouched > @IdleAfter
              AND NOT EXISTS (
                  SELECT 1 FROM ReminderLog r
                  WHERE r.Kind = @Kind AND r.ReferenceId = c.CustomerId AND r.SentOnUtc >= c.LastTouched)
            ORDER BY c.LastTouched, c.CustomerId
            """;
        cmd.Parameters.AddWithValue("@Take", take);
        cmd.Parameters.AddWithValue("@Kind", ReminderKinds.AbandonedCart);
        Add(cmd, "@IdleBefore", idleBeforeUtc);
        Add(cmd, "@IdleAfter", idleAfterUtc);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<AbandonedCartCandidate>();
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new AbandonedCartCandidate(reader.GetInt32(0), reader.GetInt32(1), DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)));
        return list;
    }

    public async Task<bool> TryRecordAsync(
        string kind, int referenceId, int customerId, DateTime nowUtc, DateTime? renewIfBeforeUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        if (renewIfBeforeUtc is not null)
        {
            // A reminder from before the cart last changed is old: renew it. Only one of two nodes changes the row.
            await using var renew = connection.CreateCommand();
            renew.CommandText = "UPDATE ReminderLog SET SentOnUtc = @Now WHERE Kind = @Kind AND ReferenceId = @Ref AND SentOnUtc < @Renew";
            renew.Parameters.AddWithValue("@Kind", kind);
            renew.Parameters.AddWithValue("@Ref", referenceId);
            Add(renew, "@Now", nowUtc);
            Add(renew, "@Renew", renewIfBeforeUtc.Value);
            if (await renew.ExecuteNonQueryAsync(cancellationToken) == 1) return true;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO ReminderLog (Kind, ReferenceId, CustomerId, SentOnUtc)
            SELECT @Kind, @Ref, @Customer, @Now
            WHERE NOT EXISTS (SELECT 1 FROM ReminderLog WHERE Kind = @Kind AND ReferenceId = @Ref)
            """;
        insert.Parameters.AddWithValue("@Kind", kind);
        insert.Parameters.AddWithValue("@Ref", referenceId);
        insert.Parameters.AddWithValue("@Customer", customerId);
        Add(insert, "@Now", nowUtc);
        try
        {
            return await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Another node recorded it between the check and the insert.
            return false;
        }
    }

    public async Task ForgetAsync(string kind, int referenceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ReminderLog WHERE Kind = @Kind AND ReferenceId = @Ref";
        cmd.Parameters.AddWithValue("@Kind", kind);
        cmd.Parameters.AddWithValue("@Ref", referenceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteOlderThanAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE TOP (@Take) FROM ReminderLog WHERE SentOnUtc < @Before";
        cmd.Parameters.AddWithValue("@Take", take);
        Add(cmd, "@Before", beforeUtc);
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
