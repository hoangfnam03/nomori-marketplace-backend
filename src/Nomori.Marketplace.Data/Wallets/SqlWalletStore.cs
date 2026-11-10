using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Wallets;

namespace Nomori.Marketplace.Data.Wallets;

public sealed class SqlWalletStore(IOptions<DatabaseOptions> options) : IWalletStore
{
    // 2601 and 2627: a unique index says the row already exists.
    private static bool IsDuplicate(SqlException ex) => ex.Number is 2601 or 2627;

    public async Task<decimal> GetBalanceAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Balance FROM Wallet WHERE CustomerId = @CustomerId";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is decimal balance ? balance : 0m;
    }

    public async Task<PagedResult<WalletEntry>> GetEntriesAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        int total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM WalletEntry WHERE CustomerId = @CustomerId";
            count.Parameters.AddWithValue("@CustomerId", customerId);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CustomerId, Kind, Amount, BalanceAfter, ReferenceType, ReferenceId, Note, CreatedOnUtc
            FROM WalletEntry WHERE CustomerId = @CustomerId
            ORDER BY Id DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
            """;
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        cmd.Parameters.AddWithValue("@Skip", (page - 1) * pageSize);
        cmd.Parameters.AddWithValue("@Take", pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<WalletEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new WalletEntry(
                reader.GetInt64(0), reader.GetInt32(1), (WalletEntryKind)reader.GetInt32(2), reader.GetDecimal(3), reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
        }
        return new PagedResult<WalletEntry>(items, total, page, pageSize);
    }

    public async Task<WalletPostResult> PostAsync(WalletPosting posting, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // The wallet row is created on first use and then locked until the commit, so two postings for one customer go one after the other.
        decimal balance;
        await using (var cmd = Command(connection, transaction, """
            IF NOT EXISTS (SELECT 1 FROM Wallet WITH (UPDLOCK, HOLDLOCK) WHERE CustomerId = @CustomerId)
                INSERT INTO Wallet (CustomerId, Balance, UpdatedOnUtc) VALUES (@CustomerId, 0, @Now);
            SELECT Balance FROM Wallet WITH (UPDLOCK) WHERE CustomerId = @CustomerId;
            """))
        {
            cmd.Parameters.AddWithValue("@CustomerId", posting.CustomerId);
            cmd.Parameters.AddWithValue("@Now", posting.NowUtc);
            balance = (decimal)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        // The same key again is the same change: it was posted once already.
        await using (var cmd = Command(connection, transaction, "SELECT 1 FROM WalletEntry WHERE IdempotencyKey = @Key"))
        {
            cmd.Parameters.Add(new SqlParameter("@Key", SqlDbType.NVarChar, WalletLimits.MaxKeyLength) { Value = posting.IdempotencyKey });
            if (await cmd.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new WalletPostResult(WalletPostOutcome.AlreadyPosted, balance);
            }
        }

        var after = balance + posting.Amount;
        if (after < 0m)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new WalletPostResult(WalletPostOutcome.Insufficient, balance);
        }

        await using (var cmd = Command(connection, transaction, """
            UPDATE Wallet SET Balance = @After, UpdatedOnUtc = @Now WHERE CustomerId = @CustomerId;
            INSERT INTO WalletEntry (CustomerId, Kind, Amount, BalanceAfter, ReferenceType, ReferenceId, IdempotencyKey, Note, CreatedOnUtc)
            VALUES (@CustomerId, @Kind, @Amount, @After, @ReferenceType, @ReferenceId, @Key, @Note, @Now);
            """))
        {
            cmd.Parameters.AddWithValue("@CustomerId", posting.CustomerId);
            cmd.Parameters.AddWithValue("@Kind", (int)posting.Kind);
            cmd.Parameters.AddWithValue("@Amount", posting.Amount);
            cmd.Parameters.AddWithValue("@After", after);
            cmd.Parameters.Add(new SqlParameter("@ReferenceType", SqlDbType.NVarChar, 30) { Value = (object?)posting.ReferenceType ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@ReferenceId", SqlDbType.Int) { Value = (object?)posting.ReferenceId ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@Key", SqlDbType.NVarChar, WalletLimits.MaxKeyLength) { Value = posting.IdempotencyKey });
            cmd.Parameters.Add(new SqlParameter("@Note", SqlDbType.NVarChar, 200) { Value = (object?)posting.Note ?? DBNull.Value });
            cmd.Parameters.AddWithValue("@Now", posting.NowUtc);
            try
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex) when (IsDuplicate(ex))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new WalletPostResult(WalletPostOutcome.AlreadyPosted, balance);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new WalletPostResult(WalletPostOutcome.Posted, after);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
