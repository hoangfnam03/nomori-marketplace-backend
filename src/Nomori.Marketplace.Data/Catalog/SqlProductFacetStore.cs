using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;

namespace Nomori.Marketplace.Data.Catalog;

/// <summary>Counts for the filter panel. Every query uses the same <see cref="ProductFilter"/> as the list, so facets never include a product the list would hide.</summary>
public sealed class SqlProductFacetStore(IOptions<DatabaseOptions> options) : IProductFacetStore
{
    private const string FromClause = "FROM Product p INNER JOIN Vendor v ON v.Id = p.VendorId";

    public async Task<ProductFacets> GetFacetsAsync(ProductQuery query, int tagLimit, CancellationToken cancellationToken)
    {
        var filter = ProductFilter.Build(query);
        await using var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var total = 0;
        decimal? min = null, max = null;
        await RunAsync(connection, filter, $"SELECT COUNT(*), MIN(p.Price), MAX(p.Price) {FromClause} WHERE {filter.Where}", async reader =>
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                total = reader.GetInt32(0);
                min = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
                max = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
            }
        }, cancellationToken);

        var manufacturers = new List<ManufacturerFacet>();
        await RunAsync(connection, filter, $"""
            SELECT fm.Id, fm.Name, COUNT(DISTINCT p.Id) {FromClause}
            INNER JOIN ProductManufacturer fpm ON fpm.ProductId = p.Id
            INNER JOIN Manufacturer fm ON fm.Id = fpm.ManufacturerId AND fm.Published = 1 AND fm.Deleted = 0
            WHERE {filter.Where}
            GROUP BY fm.Id, fm.Name ORDER BY COUNT(DISTINCT p.Id) DESC, fm.Name
            """, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken)) manufacturers.Add(new ManufacturerFacet(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        }, cancellationToken);

        var tags = new List<TagFacet>();
        await RunAsync(connection, filter, $"""
            SELECT TOP ({Math.Clamp(tagLimit, 1, 100)}) ft.Name, COUNT(DISTINCT p.Id) {FromClause}
            INNER JOIN ProductProductTag fpt ON fpt.ProductId = p.Id
            INNER JOIN ProductTag ft ON ft.Id = fpt.ProductTagId
            WHERE {filter.Where}
            GROUP BY ft.Name ORDER BY COUNT(DISTINCT p.Id) DESC, ft.Name
            """, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken)) tags.Add(new TagFacet(reader.GetString(0), reader.GetInt32(1)));
        }, cancellationToken);

        var specs = new List<SpecAttributeFacet>();
        await RunAsync(connection, filter, $"""
            SELECT fsa.Id, fsa.Name, fo.Id, fo.Name, COUNT(DISTINCT p.Id) {FromClause}
            INNER JOIN ProductSpecificationAttribute fpsa ON fpsa.ProductId = p.Id AND fpsa.AllowFiltering = 1 AND fpsa.AttributeType = 0
            INNER JOIN SpecificationAttributeOption fo ON fo.Id = fpsa.SpecificationAttributeOptionId
            INNER JOIN SpecificationAttribute fsa ON fsa.Id = fo.SpecificationAttributeId
            WHERE {filter.Where}
            GROUP BY fsa.Id, fsa.Name, fsa.DisplayOrder, fo.Id, fo.Name, fo.DisplayOrder
            ORDER BY fsa.DisplayOrder, fsa.Name, fo.DisplayOrder, fo.Name
            """, async reader =>
        {
            SpecAttributeFacet? current = null;
            var options = new List<SpecOptionFacet>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var attributeId = reader.GetInt32(0);
                if (current is null || current.Id != attributeId)
                {
                    if (current is not null) specs.Add(current with { Options = options });
                    current = new SpecAttributeFacet(attributeId, reader.GetString(1), []);
                    options = [];
                }
                options.Add(new SpecOptionFacet(reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4)));
            }
            if (current is not null) specs.Add(current with { Options = options });
        }, cancellationToken);

        return new ProductFacets(total, min, max, manufacturers, tags, specs);
    }

    private static async Task RunAsync(
        SqlConnection connection, ProductFilter filter, string sql, Func<SqlDataReader, Task> read, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        filter.Apply(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await read(reader);
    }
}
