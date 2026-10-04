using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Payments;

namespace Nomori.Marketplace.Data.Payments;

public sealed class SqlPaymentStore(IOptions<DatabaseOptions> options) : IPaymentStore
{
    private const string Columns =
        "Id, ReferenceType, ReferenceId, IdempotencyKey, Method, CustomerId, Amount, CurrencyCode, Status, RefundedAmount, ProviderReference, FailureCode, CreatedOnUtc, UpdatedOnUtc";

    // 2601 and 2627: a unique index says the row already exists.
    private static bool IsDuplicate(SqlException ex) => ex.Number is 2601 or 2627;

    public async Task<IReadOnlyList<PaymentMethodSetting>> GetMethodSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SystemName, Enabled, DisplayOrder, UpdatedOnUtc FROM PaymentMethod ORDER BY DisplayOrder, SystemName";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<PaymentMethodSetting>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new PaymentMethodSetting
            {
                SystemName = reader.GetString(0), Enabled = reader.GetBoolean(1), DisplayOrder = reader.GetInt32(2),
                UpdatedOnUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)
            });
        }
        return list;
    }

    public async Task<bool> UpdateMethodSettingAsync(PaymentMethodSetting setting, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE PaymentMethod SET Enabled = @Enabled, DisplayOrder = @DisplayOrder, UpdatedOnUtc = @Now WHERE SystemName = @SystemName";
        cmd.Parameters.AddWithValue("@SystemName", setting.SystemName);
        cmd.Parameters.AddWithValue("@Enabled", setting.Enabled);
        cmd.Parameters.AddWithValue("@DisplayOrder", setting.DisplayOrder);
        cmd.Parameters.AddWithValue("@Now", setting.UpdatedOnUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public Task<PaymentTransaction?> GetAsync(int id, CancellationToken cancellationToken) =>
        QueryOneAsync("Id = @P", id, cancellationToken);

    public Task<PaymentTransaction?> GetByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        QueryOneAsync("IdempotencyKey = @P", idempotencyKey, cancellationToken);

    public async Task<PaymentTransaction?> GetByProviderReferenceAsync(string method, string providerReference, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM PaymentTransaction WHERE Method = @Method AND ProviderReference = @Reference";
        cmd.Parameters.AddWithValue("@Method", method);
        cmd.Parameters.AddWithValue("@Reference", providerReference);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<int> InsertAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PaymentTransaction (ReferenceType, ReferenceId, IdempotencyKey, Method, CustomerId, Amount, CurrencyCode, Status, RefundedAmount, ProviderReference, FailureCode, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@ReferenceType, @ReferenceId, @Key, @Method, @CustomerId, @Amount, @CurrencyCode, @Status, 0, NULL, NULL, @Now, @Now)
            """;
        cmd.Parameters.AddWithValue("@ReferenceType", payment.ReferenceType);
        cmd.Parameters.AddWithValue("@ReferenceId", payment.ReferenceId);
        cmd.Parameters.AddWithValue("@Key", payment.IdempotencyKey);
        cmd.Parameters.AddWithValue("@Method", payment.Method);
        cmd.Parameters.AddWithValue("@CustomerId", (object?)payment.CustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Amount", payment.Amount);
        cmd.Parameters.AddWithValue("@CurrencyCode", payment.CurrencyCode);
        cmd.Parameters.AddWithValue("@Status", (int)payment.Status);
        cmd.Parameters.AddWithValue("@Now", payment.CreatedOnUtc);
        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            return 0;
        }
    }

    public async Task<bool> TryChangeAsync(
        int id, PaymentStatus expected, PaymentStatus status, decimal refundedAmount, string? providerReference, string? failureCode,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // The expected status is part of the condition: of two racing requests only one changes the row.
        cmd.CommandText = """
            UPDATE PaymentTransaction
            SET Status = @Status, RefundedAmount = @Refunded, ProviderReference = @Reference, FailureCode = @FailureCode, UpdatedOnUtc = @Now
            WHERE Id = @Id AND Status = @Expected
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Expected", (int)expected);
        cmd.Parameters.AddWithValue("@Status", (int)status);
        cmd.Parameters.AddWithValue("@Refunded", refundedAmount);
        cmd.Parameters.AddWithValue("@Reference", (object?)providerReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FailureCode", (object?)failureCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Now", nowUtc);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<PagedResult<PaymentTransaction>> GetPagedAsync(PaymentQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var where = query.Status is null ? string.Empty : "WHERE Status = @Status";

        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM PaymentTransaction {where}";
        if (query.Status is { } s) count.Parameters.AddWithValue("@Status", (int)s);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM PaymentTransaction {where} ORDER BY Id DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        if (query.Status is { } status) cmd.Parameters.AddWithValue("@Status", (int)status);
        cmd.Parameters.AddWithValue("@Skip", (query.Page - 1) * query.PageSize);
        cmd.Parameters.AddWithValue("@Take", query.PageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<PaymentTransaction>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return new PagedResult<PaymentTransaction>(items, total, query.Page, query.PageSize);
    }

    public async Task<bool> TryAddEventAsync(
        string provider, string eventId, string type, int? transactionId, string outcome, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PaymentEvent (TransactionId, Provider, ProviderEventId, Type, Outcome, CreatedOnUtc)
            VALUES (@TransactionId, @Provider, @EventId, @Type, @Outcome, @Now)
            """;
        cmd.Parameters.AddWithValue("@TransactionId", (object?)transactionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Provider", provider);
        cmd.Parameters.AddWithValue("@EventId", eventId);
        cmd.Parameters.AddWithValue("@Type", type);
        cmd.Parameters.AddWithValue("@Outcome", outcome);
        cmd.Parameters.AddWithValue("@Now", nowUtc);
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            return false;
        }
    }

    private async Task<PaymentTransaction?> QueryOneAsync(string condition, object value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM PaymentTransaction WHERE {condition}";
        cmd.Parameters.AddWithValue("@P", value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static PaymentTransaction Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        ReferenceType = r.GetString(1),
        ReferenceId = r.GetInt32(2),
        IdempotencyKey = r.GetString(3),
        Method = r.GetString(4),
        CustomerId = r.IsDBNull(5) ? null : r.GetInt32(5),
        Amount = r.GetDecimal(6),
        CurrencyCode = r.GetString(7),
        Status = (PaymentStatus)r.GetInt32(8),
        RefundedAmount = r.GetDecimal(9),
        ProviderReference = r.IsDBNull(10) ? null : r.GetString(10),
        FailureCode = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(12), DateTimeKind.Utc),
        UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(13), DateTimeKind.Utc)
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
