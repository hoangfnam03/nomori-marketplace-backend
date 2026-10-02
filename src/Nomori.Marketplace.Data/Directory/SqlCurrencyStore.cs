using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Directory;

namespace Nomori.Marketplace.Data.Directory;

public sealed class SqlCurrencyStore(IOptions<DatabaseOptions> options) : ICurrencyStore
{
    private const string Columns =
        "Id, Code, Name, Symbol, DecimalPlaces, RateToPrimary, IsPrimary, Published, DisplayOrder, RateUpdatedOnUtc, CreatedOnUtc, UpdatedOnUtc";

    public async Task<IReadOnlyList<Currency>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM Currency ORDER BY IsPrimary DESC, DisplayOrder, Code";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<Currency>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(Read(reader));
        return list;
    }

    public Task<Currency?> GetAsync(int id, CancellationToken cancellationToken) =>
        SingleAsync("WHERE Id = @Value", id, cancellationToken);

    public Task<Currency?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        SingleAsync("WHERE Code = @Value", code, cancellationToken);

    public Task<Currency?> GetPrimaryAsync(CancellationToken cancellationToken) =>
        SingleAsync("WHERE IsPrimary = 1", null, cancellationToken);

    public async Task<int> InsertAsync(Currency currency, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Currency (Code, Name, Symbol, DecimalPlaces, RateToPrimary, IsPrimary, Published, DisplayOrder, RateUpdatedOnUtc, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Code, @Name, @Symbol, @DecimalPlaces, @Rate, 0, @Published, @DisplayOrder, @RateUpdatedOnUtc, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        cmd.Parameters.AddWithValue("@Code", currency.Code);
        AddWriteParams(cmd, currency);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", currency.CreatedOnUtc);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateAsync(Currency currency, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // The code and the primary flag are deliberately absent: see MakePrimaryAsync.
        cmd.CommandText = """
            UPDATE Currency SET Name = @Name, Symbol = @Symbol, DecimalPlaces = @DecimalPlaces, RateToPrimary = @Rate,
                Published = @Published, DisplayOrder = @DisplayOrder, RateUpdatedOnUtc = @RateUpdatedOnUtc, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", currency.Id);
        AddWriteParams(cmd, currency);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // The primary is never deleted, even if a caller forgets the rule.
        cmd.CommandText = "DELETE FROM Currency WHERE Id = @Id AND IsPrimary = 0";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MakePrimaryAsync(int id, IReadOnlyDictionary<int, decimal> newRates, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // The filtered unique index allows one primary, so the old flag goes first.
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE Currency SET IsPrimary = 0 WHERE IsPrimary = 1";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (currencyId, rate) in newRates)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Currency SET RateToPrimary = @Rate, RateUpdatedOnUtc = @Now, UpdatedOnUtc = @Now,
                    IsPrimary = CASE WHEN Id = @Primary THEN 1 ELSE 0 END,
                    Published = CASE WHEN Id = @Primary THEN 1 ELSE Published END
                WHERE Id = @Id
                """;
            update.Parameters.AddWithValue("@Rate", rate);
            update.Parameters.AddWithValue("@Now", nowUtc);
            update.Parameters.AddWithValue("@Primary", id);
            update.Parameters.AddWithValue("@Id", currencyId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> AnyProductAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 1 FROM Product";
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<Currency?> SingleAsync(string where, object? value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM Currency {where}";
        if (value is not null) cmd.Parameters.AddWithValue("@Value", value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static void AddWriteParams(SqlCommand cmd, Currency c)
    {
        cmd.Parameters.AddWithValue("@Name", c.Name);
        cmd.Parameters.AddWithValue("@Symbol", (object?)c.Symbol ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DecimalPlaces", c.DecimalPlaces);
        cmd.Parameters.AddWithValue("@Rate", c.RateToPrimary);
        cmd.Parameters.AddWithValue("@Published", c.Published);
        cmd.Parameters.AddWithValue("@DisplayOrder", c.DisplayOrder);
        cmd.Parameters.AddWithValue("@RateUpdatedOnUtc", c.RateUpdatedOnUtc);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", c.UpdatedOnUtc);
    }

    private static Currency Read(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Code = r.GetString(1).Trim(),
        Name = r.GetString(2),
        Symbol = r.IsDBNull(3) ? null : r.GetString(3),
        DecimalPlaces = r.GetInt32(4),
        RateToPrimary = r.GetDecimal(5),
        IsPrimary = r.GetBoolean(6),
        Published = r.GetBoolean(7),
        DisplayOrder = r.GetInt32(8),
        RateUpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(9), DateTimeKind.Utc),
        CreatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc),
        UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc)
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
