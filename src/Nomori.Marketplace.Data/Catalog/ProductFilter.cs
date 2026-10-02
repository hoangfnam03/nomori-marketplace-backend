using Microsoft.Data.SqlClient;
using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Data.Catalog;

/// <summary>
/// The WHERE clause, sort and parameters of a <see cref="ProductQuery"/>, shared by the product list and the facet queries so that
/// both always apply the same rules. The query runs over <c>Product p INNER JOIN Vendor v</c>. Parameter names are generated here
/// (<c>@s0</c>, <c>@m1</c>, ...), user text only ever travels as a parameter value.
/// </summary>
internal sealed class ProductFilter
{
    /// <summary>
    /// The price a customer pays for one unit before variants and quantity: the special price while its window is open, otherwise the price.
    /// The price filter, the price sorts and the facet range all use it, so a product on sale is found at its sale price.
    /// </summary>
    public const string PriceExpression =
        "(CASE WHEN p.SpecialPrice IS NOT NULL AND (p.SpecialPriceStartUtc IS NULL OR p.SpecialPriceStartUtc <= SYSUTCDATETIME()) " +
        "AND (p.SpecialPriceEndUtc IS NULL OR p.SpecialPriceEndUtc > SYSUTCDATETIME()) THEN p.SpecialPrice ELSE p.Price END)";

    private readonly List<(string Name, object Value)> parameters = [];

    /// <summary>The parameters the clause refers to, for tests and for <see cref="Apply"/>.</summary>
    public IReadOnlyList<(string Name, object Value)> Parameters => parameters;

    public string Where { get; private set; } = "1 = 1";
    public string Sort { get; private set; } = "p.DisplayOrder ASC, p.Name ASC, p.Id ASC";

    public void Apply(SqlCommand cmd)
    {
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
    }

    public static ProductFilter Build(ProductQuery q)
    {
        var filter = new ProductFilter();
        var parts = new List<string> { "p.Deleted = 0" };

        if (q.CategoryIds is { Length: > 0 } categoryIds)
            parts.Add($"p.Id IN (SELECT ProductId FROM ProductCategory WHERE CategoryId IN ({filter.List("c", categoryIds.Cast<object>())}))");
        else if (q.CategoryId.HasValue)
            parts.Add($"p.Id IN (SELECT ProductId FROM ProductCategory WHERE CategoryId = {filter.Add("@CategoryId", q.CategoryId.Value)})");

        if (q.ManufacturerIds is { Length: > 0 } manufacturerIds)
            parts.Add($"p.Id IN (SELECT ProductId FROM ProductManufacturer WHERE ManufacturerId IN ({filter.List("m", manufacturerIds.Cast<object>())}))");
        else if (q.ManufacturerId.HasValue)
            parts.Add($"p.Id IN (SELECT ProductId FROM ProductManufacturer WHERE ManufacturerId = {filter.Add("@ManufacturerId", q.ManufacturerId.Value)})");

        if (q.MinPrice.HasValue) parts.Add($"{PriceExpression} >= {filter.Add("@MinPrice", q.MinPrice.Value)}");
        if (q.MaxPrice.HasValue) parts.Add($"{PriceExpression} <= {filter.Add("@MaxPrice", q.MaxPrice.Value)}");

        var terms = SearchText.Terms(q.Search);
        for (var i = 0; i < terms.Length; i++)
        {
            var like = filter.Add($"@s{i}", $"%{SearchText.EscapeLike(terms[i])}%");
            // Every term must match somewhere: name, short description, SKU, a tag or a manufacturer.
            parts.Add($"""
                (p.Name LIKE {like} OR p.ShortDescription LIKE {like} OR p.Sku LIKE {like}
                 OR EXISTS (SELECT 1 FROM ProductProductTag pt INNER JOIN ProductTag t ON t.Id = pt.ProductTagId WHERE pt.ProductId = p.Id AND t.Name LIKE {like})
                 OR EXISTS (SELECT 1 FROM ProductManufacturer pm INNER JOIN Manufacturer m ON m.Id = pm.ManufacturerId WHERE pm.ProductId = p.Id AND m.Name LIKE {like}))
                """);
        }

        if (q.Tags is { Length: > 0 } tags)
            parts.Add($"p.Id IN (SELECT pt.ProductId FROM ProductProductTag pt INNER JOIN ProductTag t ON t.Id = pt.ProductTagId WHERE t.Name IN ({filter.List("t", tags.Cast<object>())}))");

        if (q.SpecOptionGroups is { Count: > 0 } groups)
        {
            for (var g = 0; g < groups.Count; g++)
            {
                parts.Add($"""
                    p.Id IN (SELECT psa.ProductId FROM ProductSpecificationAttribute psa
                             WHERE psa.AllowFiltering = 1 AND psa.AttributeType = 0 AND psa.SpecificationAttributeOptionId IN ({filter.List($"g{g}_", groups[g].Cast<object>())}))
                    """);
            }
        }

        if (q.InStockOnly)
        {
            // Not tracked, or more on hand than active reservations hold (F12-A).
            parts.Add("""
                (p.TrackInventory = 0 OR p.StockQuantity > COALESCE((SELECT SUM(r.Quantity) FROM StockReservation r
                    WHERE r.ProductId = p.Id AND r.Status = 0 AND r.ExpiresOnUtc > SYSUTCDATETIME()), 0))
                """);
        }

        if (q.Published.HasValue) parts.Add($"p.Published = {filter.Add("@Published", q.Published.Value)}");
        if (q.VendorId.HasValue) parts.Add($"p.VendorId = {filter.Add("@VendorId", q.VendorId.Value)}");
        if (q.Status.HasValue) parts.Add($"p.Status = {filter.Add("@Status", (int)q.Status.Value)}");
        if (q.ReviewRequested == true) parts.Add("p.ReviewRequestedOnUtc IS NOT NULL");
        if (q.LowStock == true) parts.Add("p.TrackInventory = 1 AND p.StockQuantity <= p.LowStockThreshold");

        // Products of deactivated or deleted shops are not shown to the public, nor are those outside their sale window.
        if (q.OnlyActiveShops)
        {
            parts.Add("v.Active = 1 AND v.Deleted = 0");
            parts.Add("(p.AvailableStartUtc IS NULL OR p.AvailableStartUtc <= SYSUTCDATETIME()) AND (p.AvailableEndUtc IS NULL OR p.AvailableEndUtc > SYSUTCDATETIME())");
        }

        filter.Where = string.Join(" AND ", parts);
        filter.Sort = SortClause(q, terms, filter);
        return filter;
    }

    private static string SortClause(ProductQuery q, string[] terms, ProductFilter filter) => q.Sort switch
    {
        ProductSortOrder.NameAsc => "p.Name ASC, p.Id ASC",
        ProductSortOrder.NameDesc => "p.Name DESC, p.Id ASC",
        ProductSortOrder.PriceAsc => $"{PriceExpression} ASC, p.Id ASC",
        ProductSortOrder.PriceDesc => $"{PriceExpression} DESC, p.Id ASC",
        ProductSortOrder.Newest => "p.CreatedOnUtc DESC, p.Id DESC",
        // Name starts with the first term, then contains it, then everything else.
        ProductSortOrder.Relevance when terms.Length > 0 =>
            $"CASE WHEN p.Name LIKE {filter.Add("@r0", $"{SearchText.EscapeLike(terms[0])}%")} THEN 0 WHEN p.Name LIKE @s0 THEN 1 ELSE 2 END, p.DisplayOrder ASC, p.Name ASC, p.Id ASC",
        _ => "p.DisplayOrder ASC, p.Name ASC, p.Id ASC"
    };

    private string Add(string name, object value)
    {
        parameters.Add((name, value));
        return name;
    }

    /// <summary>Adds one parameter per value (<c>@{prefix}0</c>, <c>@{prefix}1</c>, ...) and returns the comma separated names.</summary>
    private string List(string prefix, IEnumerable<object> values) =>
        string.Join(",", values.Select((value, i) => Add($"@{prefix}{i}", value)));
}
