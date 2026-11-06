using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Data.Shipping;

public sealed class SqlGhnStore(IOptions<DatabaseOptions> options) : IGhnStore
{
    public async Task<GhnDestination?> GetDestinationAsync(int orderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT RecipientName, RecipientPhone, Address1, Address2, City, StateProvince, PaymentMethod, GhnProvinceId, GhnProvinceName, GhnWardId, GhnWardName
            FROM CustomerOrder WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", orderId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        return new GhnDestination(
            r.GetString(0), r.GetString(1), r.GetString(2), Text(r, 3), r.GetString(4), Text(r, 5), r.GetString(6),
            Number(r, 7), Text(r, 8), Number(r, 9), Text(r, 10));
    }

    public async Task<GhnShopOrderRef?> FindByOrderCodeAsync(string orderCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1 Id, VendorId, OrderId, Subtotal, DiscountAmount, ParcelWeightGrams, CarrierFee
            FROM ShopOrder WHERE Carrier = @Carrier AND TrackingNumber = @Code ORDER BY Id DESC
            """;
        cmd.Parameters.AddWithValue("@Carrier", GhnRules.CarrierName);
        cmd.Parameters.AddWithValue("@Code", orderCode);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        return new GhnShopOrderRef(
            r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetDecimal(3), r.GetDecimal(4), Number(r, 5), r.IsDBNull(6) ? null : r.GetDecimal(6));
    }

    public async Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT VendorId, ContactName, Phone, Address, ProvinceId, ProvinceName, WardId, WardName, FreeShippingOverSubtotal, UpdatedOnUtc
            FROM VendorGhnPickup WHERE VendorId = @VendorId
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        return new GhnPickup
        {
            VendorId = r.GetInt32(0), ContactName = r.GetString(1), Phone = r.GetString(2), Address = r.GetString(3), ProvinceId = r.GetInt32(4),
            ProvinceName = r.GetString(5), WardId = r.GetInt32(6), WardName = r.GetString(7), FreeShippingOverSubtotal = r.IsDBNull(8) ? null : r.GetDecimal(8),
            UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(9), DateTimeKind.Utc)
        };
    }

    public async Task SavePickupAsync(GhnPickup pickup, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // One row per shop: update it, or add it the first time.
        cmd.CommandText = """
            UPDATE VendorGhnPickup WITH (UPDLOCK, SERIALIZABLE) SET ContactName = @ContactName, Phone = @Phone, Address = @Address,
                ProvinceId = @ProvinceId, ProvinceName = @ProvinceName, WardId = @WardId, WardName = @WardName,
                FreeShippingOverSubtotal = @FreeOver, UpdatedOnUtc = @Now
            WHERE VendorId = @VendorId;
            IF @@ROWCOUNT = 0
                INSERT INTO VendorGhnPickup (VendorId, ContactName, Phone, Address, ProvinceId, ProvinceName, WardId, WardName, FreeShippingOverSubtotal, UpdatedOnUtc)
                VALUES (@VendorId, @ContactName, @Phone, @Address, @ProvinceId, @ProvinceName, @WardId, @WardName, @FreeOver, @Now);
            """;
        cmd.Parameters.AddWithValue("@VendorId", pickup.VendorId);
        cmd.Parameters.AddWithValue("@ContactName", pickup.ContactName);
        cmd.Parameters.AddWithValue("@Phone", pickup.Phone);
        cmd.Parameters.AddWithValue("@Address", pickup.Address);
        cmd.Parameters.AddWithValue("@ProvinceId", pickup.ProvinceId);
        cmd.Parameters.AddWithValue("@ProvinceName", pickup.ProvinceName);
        cmd.Parameters.AddWithValue("@WardId", pickup.WardId);
        cmd.Parameters.AddWithValue("@WardName", pickup.WardName);
        cmd.Parameters.AddWithValue("@FreeOver", (object?)pickup.FreeShippingOverSubtotal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Now", pickup.UpdatedOnUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeletePickupAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM VendorGhnPickup WHERE VendorId = @VendorId";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyDictionary<int, GhnProductSize>> GetProductSizesAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken)
    {
        var sizes = new Dictionary<int, GhnProductSize>();
        var ids = productIds.Distinct().ToList();
        if (ids.Count == 0) return sizes;

        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        var names = ids.Select((id, i) => (Name: $"@P{i}", Id: id)).ToList();
        cmd.CommandText = $"SELECT Id, WeightGrams, LengthCm, WidthCm, HeightCm FROM Product WHERE Id IN ({string.Join(", ", names.Select(n => n.Name))})";
        foreach (var (name, id) in names) cmd.Parameters.AddWithValue(name, id);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            sizes[r.GetInt32(0)] = new GhnProductSize(Number(r, 1), Number(r, 2), Number(r, 3), Number(r, 4));
        return sizes;
    }

    public async Task SaveShipmentAsync(GhnShipment shipment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ShopOrder SET CarrierFee = @Fee, ParcelWeightGrams = @Weight, CodAmount = @Cod, ExpectedDeliveryUtc = @Expected, ShippingAdjustment = 0
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", shipment.ShopOrderId);
        cmd.Parameters.AddWithValue("@Fee", shipment.CarrierFee);
        cmd.Parameters.AddWithValue("@Weight", shipment.ParcelWeightGrams);
        cmd.Parameters.AddWithValue("@Cod", shipment.CodAmount);
        cmd.Parameters.AddWithValue("@Expected", (object?)shipment.ExpectedDeliveryUtc ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetShippingAdjustmentAsync(int shopOrderId, decimal amount, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ShopOrder SET ShippingAdjustment = @Amount WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", shopOrderId);
        cmd.Parameters.AddWithValue("@Amount", Math.Max(0m, amount));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkCodTransferredAsync(int shopOrderId, DateTime transferredOnUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ShopOrder SET CodTransferredOnUtc = @At WHERE Id = @Id AND CodTransferredOnUtc IS NULL";
        cmd.Parameters.AddWithValue("@Id", shopOrderId);
        cmd.Parameters.AddWithValue("@At", transferredOnUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string? Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static int? Number(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
}
