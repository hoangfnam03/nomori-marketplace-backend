using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Domain.Customers;

namespace Nomori.Marketplace.Data.Customers;

public sealed class SqlCustomerIdentityStore(IOptions<DatabaseOptions> databaseOptions) : ICustomerIdentityStore
{
    public async Task<Customer?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CustomerGuid, Email, Username, Active, Deleted, FailedLoginAttempts, CannotLoginUntilDateUtc, RequireReLogin, CreatedOnUtc, LastLoginDateUtc FROM Customer WHERE Email = @Email";
        command.Parameters.AddWithValue("@Email", email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCustomer(reader) : null;
    }

    public async Task<Customer?> FindByIdAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CustomerGuid, Email, Username, Active, Deleted, FailedLoginAttempts, CannotLoginUntilDateUtc, RequireReLogin, CreatedOnUtc, LastLoginDateUtc FROM Customer WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCustomer(reader) : null;
    }

    public async Task<CustomerPassword?> GetLatestPasswordAsync(int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (1) Id, CustomerId, Password, PasswordFormatId, PasswordSalt, CreatedOnUtc FROM CustomerPassword WHERE CustomerId = @CustomerId ORDER BY CreatedOnUtc DESC, Id DESC";
        command.Parameters.AddWithValue("@CustomerId", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new CustomerPassword
        {
            Id = reader.GetInt32(0), CustomerId = reader.GetInt32(1), Password = reader.GetString(2),
            PasswordFormat = (PasswordFormat)reader.GetInt32(3), PasswordSalt = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedOnUtc = reader.GetDateTime(5)
        };
    }

    public async Task<int> CreateCustomerAsync(Customer customer, CustomerPassword password, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var customerCommand = connection.CreateCommand();
        customerCommand.Transaction = (SqlTransaction)transaction;
        customerCommand.CommandText = "INSERT INTO Customer (CustomerGuid, Email, Username, Active, Deleted, FailedLoginAttempts, RequireReLogin, CreatedOnUtc) OUTPUT INSERTED.Id VALUES (@CustomerGuid, @Email, @Username, @Active, @Deleted, @FailedLoginAttempts, @RequireReLogin, @CreatedOnUtc)";
        customerCommand.Parameters.AddWithValue("@CustomerGuid", customer.CustomerGuid);
        customerCommand.Parameters.AddWithValue("@Email", customer.Email);
        customerCommand.Parameters.AddWithValue("@Username", (object?)customer.Username ?? DBNull.Value);
        customerCommand.Parameters.AddWithValue("@Active", customer.Active);
        customerCommand.Parameters.AddWithValue("@Deleted", customer.Deleted);
        customerCommand.Parameters.AddWithValue("@FailedLoginAttempts", customer.FailedLoginAttempts);
        customerCommand.Parameters.AddWithValue("@RequireReLogin", customer.RequireReLogin);
        customerCommand.Parameters.AddWithValue("@CreatedOnUtc", customer.CreatedOnUtc);
        var customerId = (int)(await customerCommand.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Customer insert did not return an id."));

        await using var passwordCommand = connection.CreateCommand();
        passwordCommand.Transaction = (SqlTransaction)transaction;
        passwordCommand.CommandText = "INSERT INTO CustomerPassword (CustomerId, Password, PasswordFormatId, PasswordSalt, CreatedOnUtc) VALUES (@CustomerId, @Password, @PasswordFormatId, @PasswordSalt, @CreatedOnUtc)";
        passwordCommand.Parameters.AddWithValue("@CustomerId", customerId);
        passwordCommand.Parameters.AddWithValue("@Password", password.Password);
        passwordCommand.Parameters.AddWithValue("@PasswordFormatId", (int)password.PasswordFormat);
        passwordCommand.Parameters.AddWithValue("@PasswordSalt", (object?)password.PasswordSalt ?? DBNull.Value);
        passwordCommand.Parameters.AddWithValue("@CreatedOnUtc", password.CreatedOnUtc);
        await passwordCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var roleCommand = connection.CreateCommand();
        roleCommand.Transaction = (SqlTransaction)transaction;
        roleCommand.CommandText = "INSERT INTO CustomerCustomerRoleMapping (CustomerId, CustomerRoleId) SELECT @CustomerId, Id FROM CustomerRole WHERE SystemName = 'Registered'";
        roleCommand.Parameters.AddWithValue("@CustomerId", customerId);
        await roleCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return customerId;
    }

    public async Task UpdateCustomerAsync(Customer customer, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Customer SET FailedLoginAttempts = @FailedLoginAttempts, CannotLoginUntilDateUtc = @CannotLoginUntilDateUtc, RequireReLogin = @RequireReLogin, LastLoginDateUtc = @LastLoginDateUtc WHERE Id = @Id";
        command.Parameters.AddWithValue("@FailedLoginAttempts", customer.FailedLoginAttempts);
        command.Parameters.AddWithValue("@CannotLoginUntilDateUtc", (object?)customer.CannotLoginUntilDateUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@RequireReLogin", customer.RequireReLogin);
        command.Parameters.AddWithValue("@LastLoginDateUtc", (object?)customer.LastLoginDateUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@Id", customer.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(databaseOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static Customer ReadCustomer(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0), CustomerGuid = reader.GetGuid(1), Email = reader.GetString(2),
        Username = reader.IsDBNull(3) ? null : reader.GetString(3), Active = reader.GetBoolean(4), Deleted = reader.GetBoolean(5),
        FailedLoginAttempts = reader.GetInt32(6), CannotLoginUntilDateUtc = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
        RequireReLogin = reader.GetBoolean(8), CreatedOnUtc = reader.GetDateTime(9), LastLoginDateUtc = reader.IsDBNull(10) ? null : reader.GetDateTime(10)
    };
}