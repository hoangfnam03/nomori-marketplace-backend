using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Tax;

namespace Nomori.Marketplace.Data.Tax;

public sealed class SqlTaxStore(IOptions<DatabaseOptions> options) : ITaxStore
{
    private const string CategoryColumns = "Id, Name, IsDefault, DisplayOrder, CreatedOnUtc, UpdatedOnUtc";
    private const string RateColumns = "Id, CategoryId, CountryCode, StateProvinceId, Percentage, Published, CreatedOnUtc, UpdatedOnUtc";

    // 2601 and 2627: a unique index says the row already exists.
    private static bool IsDuplicate(SqlException ex) => ex.Number is 2601 or 2627;

    // ---- Categories ----

    public async Task<IReadOnlyList<TaxCategory>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {CategoryColumns} FROM TaxCategory ORDER BY DisplayOrder, Name";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<TaxCategory>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(ReadCategory(reader));
        return list;
    }

    public async Task<TaxCategory?> GetCategoryAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {CategoryColumns} FROM TaxCategory WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCategory(reader) : null;
    }

    public async Task<int> InsertCategoryAsync(TaxCategory category, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TaxCategory (Name, IsDefault, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id VALUES (@Name, 0, @DisplayOrder, @Now, @Now)
            """;
        cmd.Parameters.AddWithValue("@Name", category.Name);
        cmd.Parameters.AddWithValue("@DisplayOrder", category.DisplayOrder);
        cmd.Parameters.AddWithValue("@Now", category.CreatedOnUtc);
        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            return 0;
        }
    }

    public async Task<bool> UpdateCategoryAsync(TaxCategory category, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // Which category is the default is not written here: it is a fact of the seed.
        cmd.CommandText = "UPDATE TaxCategory SET Name = @Name, DisplayOrder = @DisplayOrder, UpdatedOnUtc = @Now WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", category.Id);
        cmd.Parameters.AddWithValue("@Name", category.Name);
        cmd.Parameters.AddWithValue("@DisplayOrder", category.DisplayOrder);
        cmd.Parameters.AddWithValue("@Now", category.UpdatedOnUtc);
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

    public Task DeleteCategoryAsync(int id, CancellationToken cancellationToken) =>
        NonQueryAsync("DELETE FROM TaxCategory WHERE Id = @Id", id, cancellationToken);

    public Task<int> CountRatesAsync(int categoryId, CancellationToken cancellationToken) =>
        CountAsync("SELECT COUNT(*) FROM TaxRate WHERE CategoryId = @Id", categoryId, cancellationToken);

    public Task<int> CountProductsAsync(int categoryId, CancellationToken cancellationToken) =>
        CountAsync("SELECT COUNT(*) FROM ProductTaxCategory WHERE TaxCategoryId = @Id", categoryId, cancellationToken);

    // ---- Rates ----

    public async Task<IReadOnlyList<TaxRate>> GetRatesAsync(int categoryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {RateColumns} FROM TaxRate WHERE CategoryId = @Id ORDER BY CountryCode, StateProvinceId, Id";
        cmd.Parameters.AddWithValue("@Id", categoryId);
        return await ReadRatesAsync(cmd, cancellationToken);
    }

    public async Task<TaxRate?> GetRateAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {RateColumns} FROM TaxRate WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        var rates = await ReadRatesAsync(cmd, cancellationToken);
        return rates.Count > 0 ? rates[0] : null;
    }

    public async Task<IReadOnlyList<TaxRate>> GetPublishedRatesAsync(string countryCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {RateColumns} FROM TaxRate WHERE Published = 1 AND CountryCode = @CountryCode";
        cmd.Parameters.AddWithValue("@CountryCode", countryCode);
        return await ReadRatesAsync(cmd, cancellationToken);
    }

    public async Task<int> InsertRateAsync(TaxRate rate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TaxRate (CategoryId, CountryCode, StateProvinceId, Percentage, Published, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id VALUES (@CategoryId, @CountryCode, @StateProvinceId, @Percentage, @Published, @Now, @Now)
            """;
        cmd.Parameters.AddWithValue("@CategoryId", rate.CategoryId);
        cmd.Parameters.AddWithValue("@Now", rate.CreatedOnUtc);
        AddRateValues(cmd, rate);
        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            return 0;
        }
    }

    public async Task<bool> UpdateRateAsync(TaxRate rate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE TaxRate SET CountryCode = @CountryCode, StateProvinceId = @StateProvinceId, Percentage = @Percentage, Published = @Published, UpdatedOnUtc = @Now
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", rate.Id);
        cmd.Parameters.AddWithValue("@Now", rate.UpdatedOnUtc);
        AddRateValues(cmd, rate);
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

    public Task DeleteRateAsync(int id, CancellationToken cancellationToken) =>
        NonQueryAsync("DELETE FROM TaxRate WHERE Id = @Id", id, cancellationToken);

    // ---- Products ----

    public async Task<IReadOnlyDictionary<int, int>> GetProductCategoryIdsAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, int>();
        if (productIds.Count == 0) return result;

        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        var names = new List<string>();
        var index = 0;
        foreach (var id in productIds)
        {
            var name = "@P" + index++;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, id);
        }
        cmd.CommandText = $"SELECT ProductId, TaxCategoryId FROM ProductTaxCategory WHERE ProductId IN ({string.Join(',', names)})";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetInt32(0)] = reader.GetInt32(1);
        return result;
    }

    public async Task SetProductCategoryAsync(int productId, int? categoryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Parameters.AddWithValue("@ProductId", productId);
        if (categoryId is null)
        {
            cmd.CommandText = "DELETE FROM ProductTaxCategory WHERE ProductId = @ProductId";
        }
        else
        {
            // Insert or replace in one statement, so two administrators cannot both insert.
            cmd.CommandText = """
                MERGE ProductTaxCategory WITH (HOLDLOCK) AS target
                USING (SELECT @ProductId AS ProductId) AS source ON target.ProductId = source.ProductId
                WHEN MATCHED THEN UPDATE SET TaxCategoryId = @CategoryId
                WHEN NOT MATCHED THEN INSERT (ProductId, TaxCategoryId) VALUES (@ProductId, @CategoryId);
                """;
            cmd.Parameters.AddWithValue("@CategoryId", categoryId.Value);
        }
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- Helpers ----

    private static void AddRateValues(SqlCommand cmd, TaxRate rate)
    {
        cmd.Parameters.AddWithValue("@CountryCode", rate.CountryCode);
        cmd.Parameters.AddWithValue("@StateProvinceId", (object?)rate.StateProvinceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Percentage", rate.Percentage);
        cmd.Parameters.AddWithValue("@Published", rate.Published);
    }

    private async Task NonQueryAsync(string sql, int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CountAsync(string sql, int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<TaxRate>> ReadRatesAsync(SqlCommand cmd, CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<TaxRate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new TaxRate
            {
                Id = reader.GetInt32(0), CategoryId = reader.GetInt32(1), CountryCode = reader.GetString(2),
                StateProvinceId = reader.IsDBNull(3) ? null : reader.GetInt32(3), Percentage = reader.GetDecimal(4), Published = reader.GetBoolean(5),
                CreatedOnUtc = DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc), UpdatedOnUtc = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)
            });
        }
        return list;
    }

    private static TaxCategory ReadCategory(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0), Name = r.GetString(1), IsDefault = r.GetBoolean(2), DisplayOrder = r.GetInt32(3),
        CreatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc)
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
