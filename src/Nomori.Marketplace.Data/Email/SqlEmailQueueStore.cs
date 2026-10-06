using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Email;

namespace Nomori.Marketplace.Data.Email;

public sealed class SqlEmailQueueStore(IOptions<DatabaseOptions> options) : IEmailQueueStore
{
    // The list and counts never read the bodies, which can be large.
    private const string ListColumns =
        "Id, Kind, ToAddress, Subject, Status, Attempts, NextAttemptUtc, LockedUntilUtc, LastError, CreatedOnUtc, SentOnUtc";

    public async Task<long> InsertAsync(QueuedEmail email, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO QueuedEmail (Kind, ToAddress, Subject, HtmlBody, TextBody, Status, Attempts, NextAttemptUtc, CreatedOnUtc)
            OUTPUT inserted.Id
            VALUES (@Kind, @To, @Subject, @Html, @Text, @Status, 0, @Next, @Created)
            """;
        cmd.Parameters.AddWithValue("@Kind", email.Kind);
        cmd.Parameters.AddWithValue("@To", email.ToAddress);
        cmd.Parameters.AddWithValue("@Subject", email.Subject);
        cmd.Parameters.AddWithValue("@Html", email.HtmlBody);
        cmd.Parameters.AddWithValue("@Text", (object?)email.TextBody ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", email.Status);
        Add(cmd, "@Next", email.NextAttemptUtc);
        Add(cmd, "@Created", email.CreatedOnUtc);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<QueuedEmail>> ClaimDueAsync(DateTime nowUtc, DateTime leaseUntilUtc, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // One statement picks and marks the emails, so two nodes (or a node and a manual run) never get the same one. READPAST skips rows
        // another statement is changing. A sending email whose lease ended (its node died) is due again.
        cmd.CommandText = """
            WITH due AS (
                SELECT TOP (@Take) *
                FROM QueuedEmail WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE (Status = 'pending' AND NextAttemptUtc <= @Now)
                   OR (Status = 'sending' AND LockedUntilUtc <= @Now)
                ORDER BY NextAttemptUtc, Id
            )
            UPDATE due
            SET Status = 'sending', LockedUntilUtc = @Until, Attempts = Attempts + 1
            OUTPUT inserted.Id, inserted.Kind, inserted.ToAddress, inserted.Subject, inserted.HtmlBody, inserted.TextBody,
                   inserted.Status, inserted.Attempts, inserted.NextAttemptUtc, inserted.LockedUntilUtc, inserted.LastError,
                   inserted.CreatedOnUtc, inserted.SentOnUtc
            """;
        cmd.Parameters.AddWithValue("@Take", take);
        Add(cmd, "@Now", nowUtc);
        Add(cmd, "@Until", leaseUntilUtc);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<QueuedEmail>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new QueuedEmail
            {
                Id = reader.GetInt64(0), Kind = reader.GetString(1), ToAddress = reader.GetString(2), Subject = reader.GetString(3),
                HtmlBody = reader.GetString(4), TextBody = reader.IsDBNull(5) ? null : reader.GetString(5), Status = reader.GetString(6),
                Attempts = reader.GetInt32(7), NextAttemptUtc = Utc(reader.GetDateTime(8)), LockedUntilUtc = NullableUtc(reader, 9),
                LastError = reader.IsDBNull(10) ? null : reader.GetString(10), CreatedOnUtc = Utc(reader.GetDateTime(11)),
                SentOnUtc = NullableUtc(reader, 12)
            });
        }
        return list.OrderBy(e => e.NextAttemptUtc).ThenBy(e => e.Id).ToList();
    }

    public async Task MarkSentAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE QueuedEmail SET Status = 'sent', SentOnUtc = @Now, LockedUntilUtc = NULL, LastError = NULL
            WHERE Id = @Id AND Status = 'sending'
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        Add(cmd, "@Now", nowUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAttemptFailedAsync(long id, string failure, DateTime? nextAttemptUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE QueuedEmail
            SET Status = CASE WHEN @Next IS NULL THEN 'failed' ELSE 'pending' END,
                NextAttemptUtc = ISNULL(@Next, NextAttemptUtc), LockedUntilUtc = NULL, LastError = @Error
            WHERE Id = @Id AND Status = 'sending'
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Error", failure);
        cmd.Parameters.Add(new SqlParameter("@Next", SqlDbType.DateTime2) { Value = (object?)nextAttemptUtc ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RetryAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE QueuedEmail SET Status = 'pending', Attempts = 0, NextAttemptUtc = @Now, LockedUntilUtc = NULL
            WHERE Id = @Id AND Status = 'failed'
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        Add(cmd, "@Now", nowUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // An email being sent (lease not over) stays; one whose lease is over is dead and may go.
        cmd.CommandText = "DELETE FROM QueuedEmail WHERE Id = @Id AND (Status <> 'sending' OR LockedUntilUtc <= @Now)";
        cmd.Parameters.AddWithValue("@Id", id);
        Add(cmd, "@Now", nowUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> DeleteOldAsync(DateTime sentBeforeUtc, DateTime failedBeforeUtc, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE TOP (@Take) FROM QueuedEmail
            WHERE (Status = 'sent' AND SentOnUtc < @SentBefore)
               OR (Status = 'failed' AND CreatedOnUtc < @FailedBefore)
            """;
        cmd.Parameters.AddWithValue("@Take", take);
        Add(cmd, "@SentBefore", sentBeforeUtc);
        Add(cmd, "@FailedBefore", failedBeforeUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PagedResult<QueuedEmail>> ListAsync(EmailQueueQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string where = """
            WHERE (@Status IS NULL OR Status = @Status)
              AND (@Search IS NULL OR ToAddress LIKE @Like ESCAPE '\' OR Subject LIKE @Like ESCAPE '\' OR Kind LIKE @Like ESCAPE '\')
            """;
        var like = query.Search is null ? null : "%" + query.Search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";

        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 20) { Value = (object?)query.Status ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@Search", SqlDbType.NVarChar, 200) { Value = (object?)query.Search ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@Like", SqlDbType.NVarChar, 400) { Value = (object?)like ?? DBNull.Value });
        }

        int total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM QueuedEmail {where}";
            Bind(count);
            total = (int)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ListColumns} FROM QueuedEmail {where}
            ORDER BY CreatedOnUtc DESC, Id DESC
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
            """;
        Bind(cmd);
        cmd.Parameters.AddWithValue("@Skip", (query.Page - 1) * query.PageSize);
        cmd.Parameters.AddWithValue("@Take", query.PageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<QueuedEmail>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadSummary(reader));
        return new PagedResult<QueuedEmail>(items, total, query.Page, query.PageSize);
    }

    public async Task<QueuedEmail?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {ListColumns}, HtmlBody, TextBody FROM QueuedEmail WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var email = ReadSummary(reader);
        email.HtmlBody = reader.GetString(11);
        email.TextBody = reader.IsDBNull(12) ? null : reader.GetString(12);
        return email;
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Status, COUNT(*) FROM QueuedEmail GROUP BY Status";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var counts = QueuedEmailStatuses.All.ToDictionary(s => s, _ => 0);
        while (await reader.ReadAsync(cancellationToken)) counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private static QueuedEmail ReadSummary(SqlDataReader r) => new()
    {
        Id = r.GetInt64(0), Kind = r.GetString(1), ToAddress = r.GetString(2), Subject = r.GetString(3), Status = r.GetString(4),
        Attempts = r.GetInt32(5), NextAttemptUtc = Utc(r.GetDateTime(6)), LockedUntilUtc = NullableUtc(r, 7),
        LastError = r.IsDBNull(8) ? null : r.GetString(8), CreatedOnUtc = Utc(r.GetDateTime(9)), SentOnUtc = NullableUtc(r, 10)
    };

    private static DateTime? NullableUtc(SqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Utc(r.GetDateTime(ordinal));

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static void Add(SqlCommand cmd, string name, DateTime value) =>
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime2) { Value = value });

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
