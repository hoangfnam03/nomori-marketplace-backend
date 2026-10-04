using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Data.Shipping;

public sealed class SqlShippingStore(IOptions<DatabaseOptions> options) : IShippingStore
{
    private const string Columns =
        "Id, VendorId, Name, CountryCode, StateProvinceId, Fee, FreeOverSubtotal, MinDays, MaxDays, Published, DisplayOrder, CreatedOnUtc, UpdatedOnUtc";

    public async Task<IReadOnlyList<ShippingRate>> GetRatesAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM ShippingRate WHERE VendorId = @VendorId ORDER BY CountryCode, DisplayOrder, Name, Id";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return await ReadAllAsync(cmd, cancellationToken);
    }

    public async Task<ShippingRate?> GetRateAsync(int vendorId, int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // The shop id is part of the condition: a rate id alone never reaches another shop's rate.
        cmd.CommandText = $"SELECT {Columns} FROM ShippingRate WHERE VendorId = @VendorId AND Id = @Id";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@Id", id);
        var rates = await ReadAllAsync(cmd, cancellationToken);
        return rates.Count > 0 ? rates[0] : null;
    }

    public async Task<IReadOnlyList<ShippingRate>> GetPublishedRatesAsync(
        IReadOnlyCollection<int> vendorIds, string countryCode, CancellationToken cancellationToken)
    {
        if (vendorIds.Count == 0) return [];

        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        var names = new List<string>();
        var index = 0;
        foreach (var vendorId in vendorIds)
        {
            var name = "@V" + index++;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, vendorId);
        }
        cmd.CommandText = $"""
            SELECT {Columns} FROM ShippingRate
            WHERE Published = 1 AND CountryCode = @CountryCode AND VendorId IN ({string.Join(',', names)})
            """;
        cmd.Parameters.AddWithValue("@CountryCode", countryCode);
        return await ReadAllAsync(cmd, cancellationToken);
    }

    public async Task<int> CountRatesAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ShippingRate WHERE VendorId = @VendorId";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> InsertAsync(ShippingRate rate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ShippingRate (VendorId, Name, CountryCode, StateProvinceId, Fee, FreeOverSubtotal, MinDays, MaxDays, Published, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@VendorId, @Name, @CountryCode, @StateProvinceId, @Fee, @FreeOverSubtotal, @MinDays, @MaxDays, @Published, @DisplayOrder, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        cmd.Parameters.AddWithValue("@VendorId", rate.VendorId);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", rate.CreatedOnUtc);
        AddValues(cmd, rate);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task UpdateAsync(ShippingRate rate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ShippingRate SET Name = @Name, CountryCode = @CountryCode, StateProvinceId = @StateProvinceId, Fee = @Fee,
                FreeOverSubtotal = @FreeOverSubtotal, MinDays = @MinDays, MaxDays = @MaxDays, Published = @Published,
                DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id AND VendorId = @VendorId
            """;
        cmd.Parameters.AddWithValue("@Id", rate.Id);
        cmd.Parameters.AddWithValue("@VendorId", rate.VendorId);
        AddValues(cmd, rate);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(int vendorId, int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ShippingRate WHERE VendorId = @VendorId AND Id = @Id";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@Id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static void AddValues(SqlCommand cmd, ShippingRate rate)
    {
        cmd.Parameters.AddWithValue("@Name", rate.Name);
        cmd.Parameters.AddWithValue("@CountryCode", rate.CountryCode);
        cmd.Parameters.AddWithValue("@StateProvinceId", (object?)rate.StateProvinceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Fee", rate.Fee);
        cmd.Parameters.AddWithValue("@FreeOverSubtotal", (object?)rate.FreeOverSubtotal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MinDays", (object?)rate.MinDays ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MaxDays", (object?)rate.MaxDays ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Published", rate.Published);
        cmd.Parameters.AddWithValue("@DisplayOrder", rate.DisplayOrder);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", rate.UpdatedOnUtc);
    }

    private static async Task<IReadOnlyList<ShippingRate>> ReadAllAsync(SqlCommand cmd, CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rates = new List<ShippingRate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rates.Add(new ShippingRate
            {
                Id = reader.GetInt32(0),
                VendorId = reader.GetInt32(1),
                Name = reader.GetString(2),
                CountryCode = reader.GetString(3),
                StateProvinceId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Fee = reader.GetDecimal(5),
                FreeOverSubtotal = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                MinDays = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                MaxDays = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Published = reader.GetBoolean(9),
                DisplayOrder = reader.GetInt32(10),
                CreatedOnUtc = DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc),
                UpdatedOnUtc = DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc)
            });
        }
        return rates;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
