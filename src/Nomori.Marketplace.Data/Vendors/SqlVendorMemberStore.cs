using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Data.Vendors;

public sealed class SqlVendorMemberStore(IOptions<DatabaseOptions> options) : IVendorMemberStore
{
    private const string SelectColumns = "c.Id, c.Email, c.FirstName, c.LastName, c.CreatedOnUtc, c.LastLoginDateUtc";

    public async Task<IReadOnlyList<VendorMember>> ListAsync(int vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Customer c WHERE c.VendorId = @VendorId AND c.Deleted = 0 ORDER BY c.CreatedOnUtc, c.Id";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var members = new List<VendorMember>();
        while (await reader.ReadAsync(cancellationToken)) members.Add(Read(reader));
        return members;
    }

    public async Task<VendorMember?> GetAsync(int vendorId, int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM Customer c WHERE c.Id = @CustomerId AND c.VendorId = @VendorId AND c.Deleted = 0";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<CreateMemberStoreResult> CreateAsync(
        int vendorId, string email, string? firstName, string? lastName,
        string passwordHash, string passwordSalt,
        string setupTokenHash, DateTime setupTokenExpiresOnUtc,
        int maxMembers, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // Serialise concurrent additions to the same shop so the member limit cannot be exceeded.
        await using (var lockCmd = connection.CreateCommand())
        {
            lockCmd.Transaction = transaction;
            lockCmd.CommandText = "SELECT COUNT(*) FROM Customer WITH (UPDLOCK, HOLDLOCK) WHERE VendorId = @VendorId";
            lockCmd.Parameters.AddWithValue("@VendorId", vendorId);
            if ((int)(await lockCmd.ExecuteScalarAsync(cancellationToken))! >= maxMembers)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CreateMemberStoreResult(CreateMemberOutcome.LimitReached);
            }
        }

        int customerId;
        try
        {
            await using var customerCmd = connection.CreateCommand();
            customerCmd.Transaction = transaction;
            customerCmd.CommandText = """
                IF EXISTS (SELECT 1 FROM Customer WHERE Email = @Email)
                    SELECT CAST(0 AS int);
                ELSE
                    INSERT INTO Customer (CustomerGuid, Email, FirstName, LastName, EmailVerified, EmailOtpEnabled, Active, Deleted,
                                          FailedLoginAttempts, RequireReLogin, CreatedOnUtc, VendorId)
                    OUTPUT INSERTED.Id
                    VALUES (NEWID(), @Email, @FirstName, @LastName, 0, 0, 1, 0, 0, 0, @NowUtc, @VendorId);
                """;
            customerCmd.Parameters.AddWithValue("@Email", email);
            customerCmd.Parameters.AddWithValue("@FirstName", (object?)firstName ?? DBNull.Value);
            customerCmd.Parameters.AddWithValue("@LastName", (object?)lastName ?? DBNull.Value);
            customerCmd.Parameters.AddWithValue("@NowUtc", nowUtc);
            customerCmd.Parameters.AddWithValue("@VendorId", vendorId);
            customerId = (int)(await customerCmd.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateMemberStoreResult(CreateMemberOutcome.EmailExists);
        }

        if (customerId == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateMemberStoreResult(CreateMemberOutcome.EmailExists);
        }

        await using var detailsCmd = connection.CreateCommand();
        detailsCmd.Transaction = transaction;
        detailsCmd.CommandText = """
            INSERT INTO CustomerPassword (CustomerId, Password, PasswordFormatId, PasswordSalt, CreatedOnUtc)
            VALUES (@CustomerId, @Password, 1, @PasswordSalt, @NowUtc);

            INSERT INTO CustomerCustomerRoleMapping (CustomerId, CustomerRoleId)
            SELECT @CustomerId, Id FROM CustomerRole WHERE SystemName IN ('Registered', 'Vendors');

            INSERT INTO PasswordRecoveryToken (CustomerId, TokenHash, ExpiresOnUtc, CreatedOnUtc)
            VALUES (@CustomerId, @TokenHash, @ExpiresOnUtc, @NowUtc);
            """;
        detailsCmd.Parameters.AddWithValue("@CustomerId", customerId);
        detailsCmd.Parameters.AddWithValue("@Password", passwordHash);
        detailsCmd.Parameters.AddWithValue("@PasswordSalt", passwordSalt);
        detailsCmd.Parameters.AddWithValue("@TokenHash", setupTokenHash);
        detailsCmd.Parameters.AddWithValue("@ExpiresOnUtc", setupTokenExpiresOnUtc);
        detailsCmd.Parameters.AddWithValue("@NowUtc", nowUtc);
        await detailsCmd.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CreateMemberStoreResult(CreateMemberOutcome.Created, customerId);
    }

    public async Task ReplaceSetupTokenAsync(
        int customerId, string tokenHash, DateTime expiresOnUtc, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE PasswordRecoveryToken SET Used = 1 WHERE CustomerId = @CustomerId AND Used = 0;

            INSERT INTO PasswordRecoveryToken (CustomerId, TokenHash, ExpiresOnUtc, CreatedOnUtc)
            VALUES (@CustomerId, @TokenHash, @ExpiresOnUtc, @NowUtc);
            """;
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        cmd.Parameters.AddWithValue("@TokenHash", tokenHash);
        cmd.Parameters.AddWithValue("@ExpiresOnUtc", expiresOnUtc);
        cmd.Parameters.AddWithValue("@NowUtc", nowUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<RemoveMemberOutcome> RemoveAsync(int vendorId, int customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var memberIds = new List<int>();
        await using (var lockCmd = connection.CreateCommand())
        {
            lockCmd.Transaction = transaction;
            lockCmd.CommandText = "SELECT Id FROM Customer WITH (UPDLOCK, HOLDLOCK) WHERE VendorId = @VendorId";
            lockCmd.Parameters.AddWithValue("@VendorId", vendorId);
            await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) memberIds.Add(reader.GetInt32(0));
        }

        if (!memberIds.Contains(customerId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return RemoveMemberOutcome.NotMember;
        }

        if (memberIds.Count <= 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RemoveMemberOutcome.LastMember;
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE m FROM CustomerCustomerRoleMapping m
            INNER JOIN CustomerRole r ON r.Id = m.CustomerRoleId
            WHERE r.SystemName = 'Vendors' AND m.CustomerId = @CustomerId;

            UPDATE Customer SET VendorId = NULL, RequireReLogin = 1 WHERE Id = @CustomerId;
            """;
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return RemoveMemberOutcome.Removed;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static VendorMember Read(SqlDataReader r) => new()
    {
        CustomerId = r.GetInt32(0),
        Email = r.GetString(1),
        FirstName = r.IsDBNull(2) ? null : r.GetString(2),
        LastName = r.IsDBNull(3) ? null : r.GetString(3),
        CreatedOnUtc = r.GetDateTime(4),
        LastLoginDateUtc = r.IsDBNull(5) ? null : r.GetDateTime(5)
    };
}
