using FluentMigrator;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Data.Catalog;
using Nomori.Marketplace.Data.Migrations.Catalog;

namespace Nomori.Marketplace.Data.Tests;

/// <summary>The SQL itself is not run here (no database); these tests pin what the builder promises: public rules, parameters for all user text, escaping.</summary>
public sealed class ProductFilterTests
{
    private static ProductQuery Public(
        string? search = null, int[]? categoryIds = null, int[]? manufacturerIds = null, string[]? tags = null,
        IReadOnlyList<int[]>? groups = null, bool inStock = false, ProductSortOrder sort = ProductSortOrder.DisplayOrder) =>
        new(1, 20, Search: search, Sort: sort, Published: true, OnlyActiveShops: true,
            CategoryIds: categoryIds, ManufacturerIds: manufacturerIds, Tags: tags, SpecOptionGroups: groups, InStockOnly: inStock);

    private static object ValueOf(ProductFilter filter, string name) => filter.Parameters.Single(p => p.Name == name).Value;

    [Fact]
    public void PublicQueriesAlwaysApplyShopStatusAndTheSaleWindow()
    {
        var filter = ProductFilter.Build(Public());

        Assert.Contains("p.Deleted = 0", filter.Where);
        Assert.Contains("p.Published = @Published", filter.Where);
        Assert.Contains("v.Active = 1 AND v.Deleted = 0", filter.Where);
        Assert.Contains("p.AvailableStartUtc", filter.Where);
        Assert.Contains("p.AvailableEndUtc", filter.Where);
    }

    [Fact]
    public void ANonPublicQueryDoesNotAddTheShopAndWindowRules()
    {
        var filter = ProductFilter.Build(new ProductQuery(1, 20, VendorId: 5));

        Assert.DoesNotContain("v.Active", filter.Where);
        Assert.Contains("p.VendorId = @VendorId", filter.Where);
    }

    [Fact]
    public void SearchTextOnlyTravelsAsEscapedParameters()
    {
        var filter = ProductFilter.Build(Public(search: "50% off'; DROP TABLE Product;--"));

        Assert.DoesNotContain("DROP", filter.Where);
        Assert.DoesNotContain("50%", filter.Where);
        Assert.Equal("%50[%]%", ValueOf(filter, "@s0"));
        // One condition per term, all required.
        Assert.Contains("@s0", filter.Where);
        Assert.Contains("@s1", filter.Where);
        Assert.Contains("p.Sku LIKE @s0", filter.Where);
        Assert.Contains("t.Name LIKE @s0", filter.Where);
        Assert.Contains("m.Name LIKE @s0", filter.Where);
    }

    [Fact]
    public void ListsAreExpandedIntoNumberedParameters()
    {
        var filter = ProductFilter.Build(Public(categoryIds: [1, 2], manufacturerIds: [7], tags: ["a", "b"]));

        Assert.Contains("CategoryId IN (@c0,@c1)", filter.Where);
        Assert.Contains("ManufacturerId IN (@m0)", filter.Where);
        Assert.Contains("t.Name IN (@t0,@t1)", filter.Where);
        Assert.Equal(1, ValueOf(filter, "@c0"));
        Assert.Equal("b", ValueOf(filter, "@t1"));
    }

    [Fact]
    public void SpecificationGroupsAreAndedAndOptionsInsideAGroupAreOred()
    {
        var filter = ProductFilter.Build(Public(groups: [[7, 8], [9]]));

        Assert.Contains("SpecificationAttributeOptionId IN (@g0_0,@g0_1)", filter.Where);
        Assert.Contains("SpecificationAttributeOptionId IN (@g1_0)", filter.Where);
        Assert.Equal(2, filter.Where.Split("p.Id IN (SELECT psa.ProductId", StringSplitOptions.None).Length - 1);
        Assert.Contains("psa.AllowFiltering = 1", filter.Where);
    }

    [Fact]
    public void InStockLooksAtReservations()
    {
        var filter = ProductFilter.Build(Public(inStock: true));

        Assert.Contains("p.TrackInventory = 0", filter.Where);
        Assert.Contains("StockReservation", filter.Where);
        Assert.Contains("r.Status = 0", filter.Where);
    }

    [Fact]
    public void RelevanceSortUsesTheFirstTermAndOtherSortsAreStable()
    {
        var relevance = ProductFilter.Build(Public(search: "mug blue", sort: ProductSortOrder.Relevance));
        Assert.Contains("p.Name LIKE @r0", relevance.Sort);
        Assert.Equal("mug%", ValueOf(relevance, "@r0"));

        // Without text there is nothing to rank by: the featured order is used.
        Assert.StartsWith("p.DisplayOrder", ProductFilter.Build(Public(sort: ProductSortOrder.Relevance)).Sort);
        Assert.EndsWith("p.Id ASC", ProductFilter.Build(Public(sort: ProductSortOrder.PriceAsc)).Sort);
    }

    [Fact]
    public void TheSellerSearchUsesTheSameSafeTerms()
    {
        var filter = ProductFilter.Build(new ProductQuery(1, 20, Search: "a_b", VendorId: 5));

        Assert.Equal("%a[_]b%", ValueOf(filter, "@s0"));
    }

    [Fact]
    public void PriceFilterAndSortsUseTheSpecialPriceWhileItsWindowIsOpen()
    {
        var filter = ProductFilter.Build(new ProductQuery(1, 20, MinPrice: 5, MaxPrice: 9, Sort: ProductSortOrder.PriceAsc, Published: true, OnlyActiveShops: true));

        Assert.Contains($"{ProductFilter.PriceExpression} >= @MinPrice", filter.Where);
        Assert.Contains($"{ProductFilter.PriceExpression} <= @MaxPrice", filter.Where);
        Assert.StartsWith(ProductFilter.PriceExpression + " ASC", filter.Sort);
        Assert.Contains("p.SpecialPriceStartUtc <= SYSUTCDATETIME()", ProductFilter.PriceExpression);
        Assert.Contains("p.SpecialPriceEndUtc > SYSUTCDATETIME()", ProductFilter.PriceExpression);
        Assert.StartsWith(ProductFilter.PriceExpression + " DESC", ProductFilter.Build(new ProductQuery(1, 20, Sort: ProductSortOrder.PriceDesc)).Sort);
    }

    [Fact]
    public void SearchIndexMigrationRunsAfterInventory()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610080001, Version(typeof(SearchIndexMigration)));
        Assert.True(Version(typeof(SearchIndexMigration)) > Version(typeof(InventoryMigration)));
    }
}
