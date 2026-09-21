using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Data.Security;

public sealed class SqlAuthorizationStore(IOptions<DatabaseOptions> databaseOptions) : IAuthorizationStore
{
    public async Task<IReadOnlyList<CustomerRole>> GetActiveRolesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, SystemName, Active, IsSystemRole FROM CustomerRole WHERE Active = 1 ORDER BY Name, Id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var roles = new List<CustomerRole>();
        while (await reader.ReadAsync(cancellationToken))
            roles.Add(ReadRole(reader));
        return roles;
    }

    public async Task<IReadOnlyList<CustomerRole>> GetCustomerRolesAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT r.Id, r.Name, r.SystemName, r.Active, r.IsSystemRole FROM CustomerRole r INNER JOIN CustomerCustomerRoleMapping crm ON crm.CustomerRoleId = r.Id WHERE crm.CustomerId = @CustomerId ORDER BY r.Name, r.Id";
        command.Parameters.AddWithValue("@CustomerId", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var roles = new List<CustomerRole>();
        while (await reader.ReadAsync(cancellationToken))
            roles.Add(ReadRole(reader));
        return roles;
    }

    public async Task<bool> CustomerExistsAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Customer WHERE Id = @CustomerId AND Deleted = 0";
        command.Parameters.AddWithValue("@CustomerId", customerId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task ReplaceCustomerRolesAsync(int customerId, IReadOnlyCollection<string> roleSystemNames, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM CustomerCustomerRoleMapping WHERE CustomerId = @CustomerId";
        deleteCommand.Parameters.AddWithValue("@CustomerId", customerId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        var roleParameters = roleSystemNames.Select((role, index) =>
        {
            var parameterName = $"@Role{index}";
            insertCommand.Parameters.AddWithValue(parameterName, role);
            return parameterName;
        }).ToArray();
        insertCommand.Parameters.AddWithValue("@CustomerId", customerId);
        insertCommand.CommandText = $"INSERT INTO CustomerCustomerRoleMapping (CustomerId, CustomerRoleId) SELECT @CustomerId, Id FROM CustomerRole WHERE Active = 1 AND SystemName IN ({string.Join(", ", roleParameters)})";
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(databaseOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static CustomerRole ReadRole(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0), Name = reader.GetString(1), SystemName = reader.GetString(2),
        Active = reader.GetBoolean(3), IsSystemRole = reader.GetBoolean(4)
    };
}
