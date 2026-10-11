using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Data.Shipping;

public sealed class SqlTrackingStore(IOptions<DatabaseOptions> options) : ITrackingStore
{
    private const string CarrierColumns = "Id, Name, TrackingUrlTemplate, Active, DisplayOrder, CreatedOnUtc, UpdatedOnUtc";

    public async Task<IReadOnlyList<Carrier>> ListCarriersAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {CarrierColumns} FROM Carrier WHERE (@ActiveOnly = 0 OR Active = 1) ORDER BY DisplayOrder, Name";
        cmd.Parameters.AddWithValue("@ActiveOnly", activeOnly);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<Carrier>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadCarrier(reader));
        return items;
    }

    public Task<Carrier?> GetCarrierAsync(int id, CancellationToken cancellationToken) =>
        QueryCarrierAsync("WHERE Id = @Value", id, cancellationToken);

    public Task<Carrier?> FindActiveCarrierByNameAsync(string name, CancellationToken cancellationToken) =>
        QueryCarrierAsync("WHERE Active = 1 AND Name = @Value", name, cancellationToken);

    private async Task<Carrier?> QueryCarrierAsync(string where, object value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT TOP (1) {CarrierColumns} FROM Carrier {where}";
        cmd.Parameters.AddWithValue("@Value", value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCarrier(reader) : null;
    }

    public async Task<int> CountCarriersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Carrier";
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> CarrierNameExistsAsync(string name, int excludeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Carrier WHERE Name = @Name AND Id <> @Id";
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Id", excludeId);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    public async Task<int> InsertCarrierAsync(Carrier carrier, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Carrier (Name, TrackingUrlTemplate, Active, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Name, @Template, @Active, @DisplayOrder, @Created, @Created)
            """;
        Bind(cmd, carrier);
        cmd.Parameters.Add(new SqlParameter("@Created", SqlDbType.DateTime2) { Value = carrier.CreatedOnUtc });
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateCarrierAsync(Carrier carrier, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Carrier SET Name = @Name, TrackingUrlTemplate = @Template, Active = @Active, DisplayOrder = @DisplayOrder, UpdatedOnUtc = @Updated
            WHERE Id = @Id
            """;
        Bind(cmd, carrier);
        cmd.Parameters.AddWithValue("@Id", carrier.Id);
        cmd.Parameters.Add(new SqlParameter("@Updated", SqlDbType.DateTime2) { Value = carrier.UpdatedOnUtc });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteCarrierAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Carrier WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<long?> TryAddEventAsync(TrackingEvent trackingEvent, int maxEvents, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // Count and insert are one statement, so two requests cannot both pass the limit.
        cmd.CommandText = """
            INSERT INTO ShopOrderTrackingEvent (ShopOrderId, Status, Location, Note, OccurredOnUtc, CreatedByCustomerId, CreatedOnUtc)
            OUTPUT INSERTED.Id
            SELECT @ShopOrderId, @Status, @Location, @Note, @Occurred, @CreatedBy, @Created
            WHERE (SELECT COUNT(*) FROM ShopOrderTrackingEvent WITH (UPDLOCK, HOLDLOCK) WHERE ShopOrderId = @ShopOrderId) < @Max
            """;
        cmd.Parameters.AddWithValue("@ShopOrderId", trackingEvent.ShopOrderId);
        cmd.Parameters.AddWithValue("@Status", trackingEvent.Status);
        cmd.Parameters.AddWithValue("@Location", (object?)trackingEvent.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Note", (object?)trackingEvent.Note ?? DBNull.Value);
        cmd.Parameters.Add(new SqlParameter("@Occurred", SqlDbType.DateTime2) { Value = trackingEvent.OccurredOnUtc });
        cmd.Parameters.AddWithValue("@CreatedBy", trackingEvent.CreatedByCustomerId);
        cmd.Parameters.Add(new SqlParameter("@Created", SqlDbType.DateTime2) { Value = trackingEvent.CreatedOnUtc });
        cmd.Parameters.AddWithValue("@Max", maxEvents);
        return await cmd.ExecuteScalarAsync(cancellationToken) as long?;
    }

    public async Task<IReadOnlyList<TrackingEvent>> ListEventsAsync(int shopOrderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ShopOrderId, Status, Location, Note, OccurredOnUtc, CreatedByCustomerId, CreatedOnUtc
            FROM ShopOrderTrackingEvent WHERE ShopOrderId = @ShopOrderId ORDER BY OccurredOnUtc DESC, Id DESC
            """;
        cmd.Parameters.AddWithValue("@ShopOrderId", shopOrderId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<TrackingEvent>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new TrackingEvent
            {
                Id = reader.GetInt64(0), ShopOrderId = reader.GetInt32(1), Status = reader.GetString(2),
                Location = reader.IsDBNull(3) ? null : reader.GetString(3), Note = reader.IsDBNull(4) ? null : reader.GetString(4),
                OccurredOnUtc = Utc(reader.GetDateTime(5)), CreatedByCustomerId = reader.GetInt32(6), CreatedOnUtc = Utc(reader.GetDateTime(7))
            });
        return items;
    }

    private static void Bind(SqlCommand cmd, Carrier carrier)
    {
        cmd.Parameters.AddWithValue("@Name", carrier.Name);
        cmd.Parameters.AddWithValue("@Template", (object?)carrier.TrackingUrlTemplate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Active", carrier.Active);
        cmd.Parameters.AddWithValue("@DisplayOrder", carrier.DisplayOrder);
    }

    private static Carrier ReadCarrier(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0), Name = r.GetString(1), TrackingUrlTemplate = r.IsDBNull(2) ? null : r.GetString(2), Active = r.GetBoolean(3),
        DisplayOrder = r.GetInt32(4), CreatedOnUtc = Utc(r.GetDateTime(5)), UpdatedOnUtc = Utc(r.GetDateTime(6))
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
