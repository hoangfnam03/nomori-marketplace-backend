using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Data.Shipping;

public sealed class SqlGhnStore(IOptions<DatabaseOptions> options) : IGhnStore
{
    public async Task<GhnDestination?> GetDestinationAsync(int orderId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT RecipientName, RecipientPhone, Address1, Address2, City, StateProvince, PaymentMethod, GhnDistrictId, GhnWardCode, GhnArea
            FROM CustomerOrder WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", orderId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        return new GhnDestination(
            r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
            r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));
    }

    public async Task<GhnShopOrderRef?> FindByOrderCodeAsync(string orderCode, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 Id, VendorId FROM ShopOrder WHERE Carrier = @Carrier AND TrackingNumber = @Code ORDER BY Id DESC";
        cmd.Parameters.AddWithValue("@Carrier", GhnRules.CarrierName);
        cmd.Parameters.AddWithValue("@Code", orderCode);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? new GhnShopOrderRef(r.GetInt32(0), r.GetInt32(1)) : null;
    }

    public async Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT VendorId, ContactName, Phone, Address, ProvinceId, ProvinceName, DistrictId, DistrictName, WardCode, WardName, UpdatedOnUtc
            FROM VendorGhnPickup WHERE VendorId = @VendorId
            """;
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        return new GhnPickup
        {
            VendorId = r.GetInt32(0), ContactName = r.GetString(1), Phone = r.GetString(2), Address = r.GetString(3), ProvinceId = r.GetInt32(4),
            ProvinceName = r.GetString(5), DistrictId = r.GetInt32(6), DistrictName = r.GetString(7), WardCode = r.GetString(8), WardName = r.GetString(9),
            UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc)
        };
    }

    public async Task SavePickupAsync(GhnPickup pickup, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // One row per shop: update it, or add it the first time.
        cmd.CommandText = """
            UPDATE VendorGhnPickup WITH (UPDLOCK, SERIALIZABLE) SET ContactName = @ContactName, Phone = @Phone, Address = @Address,
                ProvinceId = @ProvinceId, ProvinceName = @ProvinceName, DistrictId = @DistrictId, DistrictName = @DistrictName,
                WardCode = @WardCode, WardName = @WardName, UpdatedOnUtc = @Now
            WHERE VendorId = @VendorId;
            IF @@ROWCOUNT = 0
                INSERT INTO VendorGhnPickup (VendorId, ContactName, Phone, Address, ProvinceId, ProvinceName, DistrictId, DistrictName, WardCode, WardName, UpdatedOnUtc)
                VALUES (@VendorId, @ContactName, @Phone, @Address, @ProvinceId, @ProvinceName, @DistrictId, @DistrictName, @WardCode, @WardName, @Now);
            """;
        cmd.Parameters.AddWithValue("@VendorId", pickup.VendorId);
        cmd.Parameters.AddWithValue("@ContactName", pickup.ContactName);
        cmd.Parameters.AddWithValue("@Phone", pickup.Phone);
        cmd.Parameters.AddWithValue("@Address", pickup.Address);
        cmd.Parameters.AddWithValue("@ProvinceId", pickup.ProvinceId);
        cmd.Parameters.AddWithValue("@ProvinceName", pickup.ProvinceName);
        cmd.Parameters.AddWithValue("@DistrictId", pickup.DistrictId);
        cmd.Parameters.AddWithValue("@DistrictName", pickup.DistrictName);
        cmd.Parameters.AddWithValue("@WardCode", pickup.WardCode);
        cmd.Parameters.AddWithValue("@WardName", pickup.WardName);
        cmd.Parameters.AddWithValue("@Now", pickup.UpdatedOnUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeletePickupAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM VendorGhnPickup WHERE VendorId = @VendorId";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
