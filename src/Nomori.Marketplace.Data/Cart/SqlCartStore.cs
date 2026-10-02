using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Cart;

public sealed class SqlCartStore(IOptions<DatabaseOptions> options) : ICartStore
{
    private const string Columns = "Id, CustomerId, ProductId, ValueIds, Quantity, AddedUnitPrice, CreatedOnUtc, UpdatedOnUtc";

    public async Task<IReadOnlyList<CartLine>> GetLinesAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM CartItem WHERE CustomerId = @CustomerId ORDER BY CreatedOnUtc, Id";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var lines = new List<CartLine>();
        while (await reader.ReadAsync(cancellationToken)) lines.Add(Read(reader));
        return lines;
    }

    public async Task<CartLine?> GetLineAsync(int customerId, int lineId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM CartItem WHERE CustomerId = @CustomerId AND Id = @Id";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        cmd.Parameters.AddWithValue("@Id", lineId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<CartLine?> FindAsync(int customerId, int productId, string valueIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM CartItem WHERE CustomerId = @CustomerId AND ProductId = @ProductId AND ValueIds = @ValueIds";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        cmd.Parameters.AddWithValue("@ProductId", productId);
        cmd.Parameters.AddWithValue("@ValueIds", valueIds);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<bool> InsertAsync(CartLine line, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CartItem (CustomerId, ProductId, ValueIds, Quantity, AddedUnitPrice, CreatedOnUtc, UpdatedOnUtc)
            VALUES (@CustomerId, @ProductId, @ValueIds, @Quantity, @AddedUnitPrice, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        cmd.Parameters.AddWithValue("@CustomerId", line.CustomerId);
        cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
        cmd.Parameters.AddWithValue("@ValueIds", line.ValueIds);
        cmd.Parameters.AddWithValue("@Quantity", line.Quantity);
        cmd.Parameters.AddWithValue("@AddedUnitPrice", line.AddedUnitPrice);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", line.CreatedOnUtc);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", line.UpdatedOnUtc);
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        // 2601 and 2627: the unique index says the same choice is already in this cart.
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return false;
        }
    }

    public async Task UpdateLineAsync(int lineId, int quantity, decimal addedUnitPrice, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE CartItem SET Quantity = @Quantity, AddedUnitPrice = @AddedUnitPrice, UpdatedOnUtc = @Now WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", lineId);
        cmd.Parameters.AddWithValue("@Quantity", quantity);
        cmd.Parameters.AddWithValue("@AddedUnitPrice", addedUnitPrice);
        cmd.Parameters.AddWithValue("@Now", nowUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetAddedUnitPriceAsync(int lineId, decimal addedUnitPrice, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE CartItem SET AddedUnitPrice = @AddedUnitPrice WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", lineId);
        cmd.Parameters.AddWithValue("@AddedUnitPrice", addedUnitPrice);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(int customerId, int lineId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // The customer id is part of the condition: a line id alone never reaches another customer's cart.
        cmd.CommandText = "DELETE FROM CartItem WHERE CustomerId = @CustomerId AND Id = @Id";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        cmd.Parameters.AddWithValue("@Id", lineId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task ClearAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM CartItem WHERE CustomerId = @CustomerId";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountUnitsAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(Quantity), 0) FROM CartItem WHERE CustomerId = @CustomerId";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CartLine Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        CustomerId = r.GetInt32(1),
        ProductId = r.GetInt32(2),
        ValueIds = r.GetString(3),
        Quantity = r.GetInt32(4),
        AddedUnitPrice = r.GetDecimal(5),
        CreatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc),
        UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc)
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
