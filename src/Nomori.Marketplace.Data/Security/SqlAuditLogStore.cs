using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Data.Security;

public sealed class SqlAuditLogStore(IOptions<DatabaseOptions> databaseOptions) : IAuditLogStore
{
    public async Task InsertAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AuditLog (EventName, CustomerId, TargetCustomerId, EntityType, EntityId, IpAddress, DetailsJson, CreatedOnUtc) VALUES (@EventName, @CustomerId, @TargetCustomerId, @EntityType, @EntityId, @IpAddress, @DetailsJson, @CreatedOnUtc)";
        command.Parameters.AddWithValue("@EventName", entry.EventName);
        command.Parameters.AddWithValue("@CustomerId", (object?)entry.CustomerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@TargetCustomerId", (object?)entry.TargetCustomerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@EntityType", (object?)entry.EntityType ?? DBNull.Value);
        command.Parameters.AddWithValue("@EntityId", (object?)entry.EntityId ?? DBNull.Value);
        command.Parameters.AddWithValue("@IpAddress", (object?)entry.IpAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("@DetailsJson", (object?)entry.DetailsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@CreatedOnUtc", entry.CreatedOnUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLog>> GetRecentAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (@Take) Id, EventName, CustomerId, TargetCustomerId, EntityType, EntityId, IpAddress, DetailsJson, CreatedOnUtc FROM AuditLog ORDER BY CreatedOnUtc DESC, Id DESC";
        command.Parameters.AddWithValue("@Take", Math.Clamp(take, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AuditLog>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AuditLog
            {
                Id = reader.GetInt32(0), EventName = reader.GetString(1),
                CustomerId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                TargetCustomerId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                EntityType = reader.IsDBNull(4) ? null : reader.GetString(4),
                EntityId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                IpAddress = reader.IsDBNull(6) ? null : reader.GetString(6),
                DetailsJson = reader.IsDBNull(7) ? null : reader.GetString(7), CreatedOnUtc = reader.GetDateTime(8)
            });
        }
        return entries;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(databaseOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
