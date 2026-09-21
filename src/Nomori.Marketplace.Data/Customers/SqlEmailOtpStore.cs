using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Customers;
using System.Security.Cryptography;

namespace Nomori.Marketplace.Data.Customers;

public sealed class SqlEmailOtpStore(IOptions<DatabaseOptions> databaseOptions) : IEmailOtpStore
{
    public async Task<EmailOtpChallenge?> GetLatestAsync(int customerId, string purpose, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (1) ChallengeId, CustomerId, Purpose, CodeHash, CreatedOnUtc, ExpiresOnUtc, LastSentOnUtc, FailedAttempts, Used FROM EmailOtpChallenge WHERE CustomerId = @CustomerId AND Purpose = @Purpose ORDER BY CreatedOnUtc DESC, Id DESC";
        command.Parameters.AddWithValue("@CustomerId", customerId);
        command.Parameters.AddWithValue("@Purpose", purpose);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task CreateAsync(EmailOtpChallenge challenge, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var invalidate = connection.CreateCommand();
        invalidate.Transaction = transaction;
        invalidate.CommandText = "UPDATE EmailOtpChallenge SET Used = 1 WHERE CustomerId = @CustomerId AND Purpose = @Purpose AND Used = 0";
        invalidate.Parameters.AddWithValue("@CustomerId", challenge.CustomerId);
        invalidate.Parameters.AddWithValue("@Purpose", challenge.Purpose);
        await invalidate.ExecuteNonQueryAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO EmailOtpChallenge (ChallengeId, CustomerId, Purpose, CodeHash, CreatedOnUtc, ExpiresOnUtc, LastSentOnUtc, FailedAttempts, Used) VALUES (@ChallengeId, @CustomerId, @Purpose, @CodeHash, @CreatedOnUtc, @ExpiresOnUtc, @LastSentOnUtc, 0, 0)";
        command.Parameters.AddWithValue("@ChallengeId", challenge.ChallengeId);
        command.Parameters.AddWithValue("@CustomerId", challenge.CustomerId);
        command.Parameters.AddWithValue("@Purpose", challenge.Purpose);
        command.Parameters.AddWithValue("@CodeHash", challenge.CodeHash);
        command.Parameters.AddWithValue("@CreatedOnUtc", challenge.CreatedOnUtc);
        command.Parameters.AddWithValue("@ExpiresOnUtc", challenge.ExpiresOnUtc);
        command.Parameters.AddWithValue("@LastSentOnUtc", (object?)challenge.LastSentOnUtc ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<EmailOtpConsumeResult> ConsumeAsync(Guid challengeId, string purpose, string codeHash, DateTime nowUtc, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT ChallengeId, CustomerId, Purpose, CodeHash, CreatedOnUtc, ExpiresOnUtc, LastSentOnUtc, FailedAttempts, Used FROM EmailOtpChallenge WITH (UPDLOCK, ROWLOCK) WHERE ChallengeId = @ChallengeId AND Purpose = @Purpose";
        read.Parameters.AddWithValue("@ChallengeId", challengeId);
        read.Parameters.AddWithValue("@Purpose", purpose);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new EmailOtpConsumeResult(false, null, "auth.otp_invalid");
        }

        var challenge = Read(reader);
        await reader.CloseAsync();

        if (challenge.Used || challenge.ExpiresOnUtc <= nowUtc || challenge.FailedAttempts >= maxAttempts)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new EmailOtpConsumeResult(false, challenge, challenge.ExpiresOnUtc <= nowUtc ? "auth.otp_expired" : "auth.otp_locked");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(challenge.CodeHash), Convert.FromHexString(codeHash)))
        {
            await using var failed = connection.CreateCommand();
            failed.Transaction = transaction;
            failed.CommandText = "UPDATE EmailOtpChallenge SET FailedAttempts = FailedAttempts + 1, Used = CASE WHEN FailedAttempts + 1 >= @MaxAttempts THEN 1 ELSE Used END WHERE ChallengeId = @ChallengeId";
            failed.Parameters.AddWithValue("@MaxAttempts", maxAttempts);
            failed.Parameters.AddWithValue("@ChallengeId", challengeId);
            await failed.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EmailOtpConsumeResult(false, challenge with { FailedAttempts = challenge.FailedAttempts + 1, Used = challenge.FailedAttempts + 1 >= maxAttempts }, challenge.FailedAttempts + 1 >= maxAttempts ? "auth.otp_locked" : "auth.otp_invalid");
        }

        await using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = "UPDATE EmailOtpChallenge SET Used = 1 WHERE ChallengeId = @ChallengeId";
        consume.Parameters.AddWithValue("@ChallengeId", challengeId);
        await consume.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EmailOtpConsumeResult(true, challenge with { Used = true }, null);
    }

    public async Task SetEmailOtpEnabledAsync(int customerId, bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Customer SET EmailOtpEnabled = @Enabled WHERE Id = @CustomerId";
        command.Parameters.AddWithValue("@Enabled", enabled);
        command.Parameters.AddWithValue("@CustomerId", customerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(databaseOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static EmailOtpChallenge Read(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4),
        reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6), reader.GetInt32(7), reader.GetBoolean(8));
}
