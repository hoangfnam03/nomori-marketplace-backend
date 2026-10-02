using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Directory;

namespace Nomori.Marketplace.Data.Directory;

public sealed class SqlDirectoryStore(IOptions<DatabaseOptions> options) : IDirectoryStore
{
    // The count of published states comes with every country read, so a form knows whether to ask for a state.
    private const string CountryColumns = """
        c.Id, c.Code, c.Alpha3, c.Name, c.Published, c.AllowsBilling, c.AllowsShipping, c.PostalCodeRequired, c.PostalCodePattern,
        c.DisplayOrder, c.CreatedOnUtc, c.UpdatedOnUtc,
        (SELECT COUNT(*) FROM StateProvince s WHERE s.CountryId = c.Id AND s.Published = 1)
        """;

    private const string StateColumns = "Id, CountryId, Code, Name, Published, DisplayOrder";

    public async Task<IReadOnlyList<Country>> GetCountriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {CountryColumns} FROM Country c ORDER BY c.DisplayOrder, c.Name";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<Country>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(ReadCountry(reader));
        return list;
    }

    public Task<Country?> GetCountryAsync(int id, CancellationToken cancellationToken) =>
        SingleCountryAsync("WHERE c.Id = @Value", id, cancellationToken);

    public Task<Country?> GetCountryByCodeAsync(string code, CancellationToken cancellationToken) =>
        SingleCountryAsync("WHERE c.Code = @Value", code, cancellationToken);

    public async Task<int> InsertCountryAsync(Country country, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Country (Code, Alpha3, Name, Published, AllowsBilling, AllowsShipping, PostalCodeRequired, PostalCodePattern, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
            OUTPUT INSERTED.Id
            VALUES (@Code, @Alpha3, @Name, @Published, @AllowsBilling, @AllowsShipping, @PostalCodeRequired, @PostalCodePattern, @DisplayOrder, @CreatedOnUtc, @UpdatedOnUtc)
            """;
        cmd.Parameters.AddWithValue("@Code", country.Code);
        cmd.Parameters.AddWithValue("@CreatedOnUtc", country.CreatedOnUtc);
        AddCountryParams(cmd, country);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateCountryAsync(Country country, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // The code is deliberately absent: it never changes.
        cmd.CommandText = """
            UPDATE Country SET Alpha3 = @Alpha3, Name = @Name, Published = @Published, AllowsBilling = @AllowsBilling, AllowsShipping = @AllowsShipping,
                PostalCodeRequired = @PostalCodeRequired, PostalCodePattern = @PostalCodePattern, DisplayOrder = @DisplayOrder, UpdatedOnUtc = @UpdatedOnUtc
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", country.Id);
        AddCountryParams(cmd, country);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteCountryAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // States go with the country (cascade); the service has already refused a country that addresses use.
        cmd.CommandText = "DELETE FROM Country WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CountryInUseAsync(string code, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 1 FROM CustomerAddress WHERE CountryCode = @Code";
        cmd.Parameters.AddWithValue("@Code", code);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<StateProvince>> GetStatesAsync(int countryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {StateColumns} FROM StateProvince WHERE CountryId = @CountryId ORDER BY DisplayOrder, Name";
        cmd.Parameters.AddWithValue("@CountryId", countryId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<StateProvince>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(ReadState(reader));
        return list;
    }

    public async Task<StateProvince?> GetStateAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {StateColumns} FROM StateProvince WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    public async Task<StateProvince?> GetStateByCodeAsync(int countryId, string code, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {StateColumns} FROM StateProvince WHERE CountryId = @CountryId AND Code = @Code";
        cmd.Parameters.AddWithValue("@CountryId", countryId);
        cmd.Parameters.AddWithValue("@Code", code);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    public async Task<int> InsertStateAsync(StateProvince state, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO StateProvince (CountryId, Code, Name, Published, DisplayOrder)
            OUTPUT INSERTED.Id VALUES (@CountryId, @Code, @Name, @Published, @DisplayOrder)
            """;
        cmd.Parameters.AddWithValue("@CountryId", state.CountryId);
        AddStateParams(cmd, state);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task UpdateStateAsync(StateProvince state, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE StateProvince SET Code = @Code, Name = @Name, Published = @Published, DisplayOrder = @DisplayOrder WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", state.Id);
        AddStateParams(cmd, state);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteStateAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM StateProvince WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> StateInUseAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 1 FROM CustomerAddress WHERE StateProvinceId = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<Country?> SingleCountryAsync(string where, object value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {CountryColumns} FROM Country c {where}";
        cmd.Parameters.AddWithValue("@Value", value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCountry(reader) : null;
    }

    private static void AddCountryParams(SqlCommand cmd, Country c)
    {
        cmd.Parameters.AddWithValue("@Alpha3", (object?)c.Alpha3 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Name", c.Name);
        cmd.Parameters.AddWithValue("@Published", c.Published);
        cmd.Parameters.AddWithValue("@AllowsBilling", c.AllowsBilling);
        cmd.Parameters.AddWithValue("@AllowsShipping", c.AllowsShipping);
        cmd.Parameters.AddWithValue("@PostalCodeRequired", c.PostalCodeRequired);
        cmd.Parameters.AddWithValue("@PostalCodePattern", (object?)c.PostalCodePattern ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayOrder", c.DisplayOrder);
        cmd.Parameters.AddWithValue("@UpdatedOnUtc", c.UpdatedOnUtc);
    }

    private static void AddStateParams(SqlCommand cmd, StateProvince s)
    {
        cmd.Parameters.AddWithValue("@Code", s.Code);
        cmd.Parameters.AddWithValue("@Name", s.Name);
        cmd.Parameters.AddWithValue("@Published", s.Published);
        cmd.Parameters.AddWithValue("@DisplayOrder", s.DisplayOrder);
    }

    private static Country ReadCountry(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Code = r.GetString(1).Trim(),
        Alpha3 = r.IsDBNull(2) ? null : r.GetString(2).Trim(),
        Name = r.GetString(3),
        Published = r.GetBoolean(4),
        AllowsBilling = r.GetBoolean(5),
        AllowsShipping = r.GetBoolean(6),
        PostalCodeRequired = r.GetBoolean(7),
        PostalCodePattern = r.IsDBNull(8) ? null : r.GetString(8),
        DisplayOrder = r.GetInt32(9),
        CreatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc),
        UpdatedOnUtc = DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc),
        PublishedStateCount = r.GetInt32(12)
    };

    private static StateProvince ReadState(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0), CountryId = r.GetInt32(1), Code = r.GetString(2), Name = r.GetString(3), Published = r.GetBoolean(4), DisplayOrder = r.GetInt32(5)
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
