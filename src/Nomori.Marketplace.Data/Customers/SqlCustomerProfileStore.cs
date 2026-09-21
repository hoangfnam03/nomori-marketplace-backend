using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Customers;

namespace Nomori.Marketplace.Data.Customers;

public sealed class SqlCustomerProfileStore(IOptions<DatabaseOptions> databaseOptions) : ICustomerProfileStore
{
    public async Task<CustomerProfile?> GetAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Email, EmailVerified, Username, FirstName, LastName, Gender, DateOfBirth, Phone FROM Customer WHERE Id = @CustomerId AND Deleted = 0";
        command.Parameters.AddWithValue("@CustomerId", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpdateAsync(CustomerProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Customer SET FirstName = @FirstName, LastName = @LastName, Gender = @Gender, DateOfBirth = @DateOfBirth, Phone = @Phone WHERE Id = @CustomerId AND Deleted = 0";
        command.Parameters.AddWithValue("@CustomerId", profile.CustomerId);
        command.Parameters.AddWithValue("@FirstName", (object?)profile.FirstName ?? DBNull.Value);
        command.Parameters.AddWithValue("@LastName", (object?)profile.LastName ?? DBNull.Value);
        command.Parameters.AddWithValue("@Gender", (object?)profile.Gender ?? DBNull.Value);
        command.Parameters.AddWithValue("@DateOfBirth", (object?)profile.DateOfBirth ?? DBNull.Value);
        command.Parameters.AddWithValue("@Phone", (object?)profile.Phone ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(databaseOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static CustomerProfile Read(SqlDataReader reader) => new()
    {
        CustomerId = reader.GetInt32(0),
        Email = reader.GetString(1),
        EmailVerified = reader.GetBoolean(2),
        Username = reader.IsDBNull(3) ? null : reader.GetString(3),
        FirstName = reader.IsDBNull(4) ? null : reader.GetString(4),
        LastName = reader.IsDBNull(5) ? null : reader.GetString(5),
        Gender = reader.IsDBNull(6) ? null : reader.GetString(6),
        DateOfBirth = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
        Phone = reader.IsDBNull(8) ? null : reader.GetString(8)
    };
}
