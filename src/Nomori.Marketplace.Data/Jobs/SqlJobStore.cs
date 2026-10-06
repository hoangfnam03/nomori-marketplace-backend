using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Jobs;

namespace Nomori.Marketplace.Data.Jobs;

public sealed class SqlJobStore(IOptions<DatabaseOptions> options) : IJobStore
{
    private const string TaskColumns =
        "SystemName, Enabled, IntervalMinutes, NextRunUtc, LockedUntilUtc, LastStartedUtc, LastFinishedUtc, LastStatus, LastMessage";

    public async Task EnsureAsync(IReadOnlyCollection<JobDefinition> definitions, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var definition in definitions)
        {
            await using var cmd = connection.CreateCommand();
            // The unique index makes two nodes starting together safe: the second insert finds the row and does nothing.
            cmd.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM ScheduledTask WHERE SystemName = @Name)
                    INSERT INTO ScheduledTask (SystemName, Enabled, IntervalMinutes, NextRunUtc, UpdatedOnUtc)
                    VALUES (@Name, 1, @Interval, NULL, @Now)
                """;
            cmd.Parameters.AddWithValue("@Name", definition.Name);
            cmd.Parameters.AddWithValue("@Interval", definition.DefaultIntervalMinutes);
            Add(cmd, "@Now", nowUtc);
            try
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // Another node created it a moment ago.
            }
        }
    }

    public async Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {TaskColumns} FROM ScheduledTask ORDER BY SystemName";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<ScheduledTask>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(ReadTask(reader));
        return list;
    }

    public async Task<ScheduledTask?> GetAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {TaskColumns} FROM ScheduledTask WHERE SystemName = @Name";
        cmd.Parameters.AddWithValue("@Name", name);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTask(reader) : null;
    }

    public async Task<bool> UpdateAsync(string name, bool enabled, int intervalMinutes, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledTask SET Enabled = @Enabled, IntervalMinutes = @Interval, UpdatedOnUtc = @Now WHERE SystemName = @Name";
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Enabled", enabled);
        cmd.Parameters.AddWithValue("@Interval", intervalMinutes);
        Add(cmd, "@Now", nowUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<string>> DueNamesAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT SystemName FROM ScheduledTask
            WHERE Enabled = 1
              AND (NextRunUtc IS NULL OR NextRunUtc <= @Now)
              AND (LockedUntilUtc IS NULL OR LockedUntilUtc <= @Now)
            ORDER BY SystemName
            """;
        Add(cmd, "@Now", nowUtc);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
        return names;
    }

    public async Task<bool> TryClaimAsync(string name, string owner, DateTime nowUtc, DateTime leaseUntilUtc, bool force, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // One statement decides who runs: the row is changed only while nobody holds an unexpired lease (and, unless forced, while it is due).
        cmd.CommandText = """
            UPDATE ScheduledTask
            SET LockedUntilUtc = @Until, LockOwner = @Owner, LastStartedUtc = @Now, LastStatus = 'running', UpdatedOnUtc = @Now
            WHERE SystemName = @Name
              AND (LockedUntilUtc IS NULL OR LockedUntilUtc <= @Now)
              AND (@Force = 1 OR (Enabled = 1 AND (NextRunUtc IS NULL OR NextRunUtc <= @Now)))
            """;
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Owner", owner);
        cmd.Parameters.AddWithValue("@Force", force);
        Add(cmd, "@Now", nowUtc);
        Add(cmd, "@Until", leaseUntilUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteAsync(string name, string owner, JobFinish finish, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // Only the owner releases the lease: a run that overran it and lost the job to someone else must not free the new owner's lock.
        cmd.CommandText = """
            UPDATE ScheduledTask
            SET LockedUntilUtc = NULL, LockOwner = NULL, LastFinishedUtc = @Finished, NextRunUtc = @Next,
                LastStatus = @Status, LastMessage = @Message, UpdatedOnUtc = @Finished
            WHERE SystemName = @Name AND LockOwner = @Owner
            """;
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Owner", owner);
        cmd.Parameters.AddWithValue("@Status", finish.Status);
        cmd.Parameters.AddWithValue("@Message", (object?)finish.Message ?? DBNull.Value);
        Add(cmd, "@Finished", finish.FinishedUtc);
        Add(cmd, "@Next", finish.NextRunUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddRunAsync(ScheduledTaskRun run, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ScheduledTaskRun (TaskName, TriggerKind, StartedUtc, FinishedUtc, Status, Processed, Failed, Message)
            VALUES (@Name, @Trigger, @Started, @Finished, @Status, @Processed, @Failed, @Message)
            """;
        cmd.Parameters.AddWithValue("@Name", run.TaskName);
        cmd.Parameters.AddWithValue("@Trigger", run.Trigger);
        cmd.Parameters.AddWithValue("@Status", run.Status);
        cmd.Parameters.AddWithValue("@Processed", run.Processed);
        cmd.Parameters.AddWithValue("@Failed", run.Failed);
        cmd.Parameters.AddWithValue("@Message", (object?)run.Message ?? DBNull.Value);
        Add(cmd, "@Started", run.StartedUtc);
        Add(cmd, "@Finished", run.FinishedUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduledTaskRun>> GetRunsAsync(string name, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@Take) Id, TaskName, TriggerKind, StartedUtc, FinishedUtc, Status, Processed, Failed, Message
            FROM ScheduledTaskRun WHERE TaskName = @Name ORDER BY StartedUtc DESC, Id DESC
            """;
        cmd.Parameters.AddWithValue("@Take", take);
        cmd.Parameters.AddWithValue("@Name", name);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var runs = new List<ScheduledTaskRun>();
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new ScheduledTaskRun
            {
                Id = reader.GetInt64(0), TaskName = reader.GetString(1), Trigger = reader.GetString(2),
                StartedUtc = Utc(reader.GetDateTime(3)), FinishedUtc = Utc(reader.GetDateTime(4)), Status = reader.GetString(5),
                Processed = reader.GetInt32(6), Failed = reader.GetInt32(7), Message = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }
        return runs;
    }

    private static ScheduledTask ReadTask(SqlDataReader r) => new()
    {
        SystemName = r.GetString(0), Enabled = r.GetBoolean(1), IntervalMinutes = r.GetInt32(2),
        NextRunUtc = NullableUtc(r, 3), LockedUntilUtc = NullableUtc(r, 4), LastStartedUtc = NullableUtc(r, 5), LastFinishedUtc = NullableUtc(r, 6),
        LastStatus = r.IsDBNull(7) ? null : r.GetString(7), LastMessage = r.IsDBNull(8) ? null : r.GetString(8)
    };

    private static DateTime? NullableUtc(SqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Utc(r.GetDateTime(ordinal));

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Times are compared with the lease, so they go as datetime2 and keep every digit.</summary>
    private static void Add(SqlCommand cmd, string name, DateTime value) =>
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime2) { Value = value });

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
