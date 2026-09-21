using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Customers;

namespace Nomori.Marketplace.Data.Customers;

public sealed class SqlEmailVerificationStore(IOptions<DatabaseOptions> databaseOptions) : IEmailVerificationStore
{
    public async Task CreateAsync(int customerId, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var invalidate = connection.CreateCommand();
        invalidate.Transaction = transaction;
        invalidate.CommandText = "UPDATE EmailVerificationToken SET Used = 1 WHERE CustomerId = @CustomerId AND Used = 0";
        invalidate.Parameters.AddWithValue("@CustomerId", customerId);
        await invalidate.ExecuteNonQueryAsync(cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO EmailVerificationToken (CustomerId, TokenHash, ExpiresOnUtc, CreatedOnUtc) VALUES (@CustomerId, @TokenHash, @ExpiresOnUtc, SYSUTCDATETIME())";
        insert.Parameters.AddWithValue("@CustomerId", customerId);
        insert.Parameters.AddWithValue("@TokenHash", tokenHash);
        insert.Parameters.AddWithValue("@ExpiresOnUtc", expiresOnUtc);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(int CustomerId, DateTime ExpiresOnUtc, bool Used)?> FindAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CustomerId, ExpiresOnUtc, Used FROM EmailVerificationToken WHERE TokenHash = @TokenHash";
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt32(0), reader.GetDateTime(1), reader.GetBoolean(2))
            : null;
    }

    public async Task<int?> ConsumeAsync(string tokenHash, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE EmailVerificationToken SET Used = 1 OUTPUT INSERTED.CustomerId WHERE TokenHash = @TokenHash AND Used = 0 AND ExpiresOnUtc > @NowUtc";
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@NowUtc", nowUtc);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : (int)value;
    }

    public async Task MarkVerifiedAsync(int customerId, DateTime verifiedOnUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Customer SET EmailVerified = 1, EmailVerifiedOnUtc = @VerifiedOnUtc WHERE Id = @CustomerId";
        command.Parameters.AddWithValue("@VerifiedOnUtc", verifiedOnUtc);
        command.Parameters.AddWithValue("@CustomerId", customerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(databaseOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
