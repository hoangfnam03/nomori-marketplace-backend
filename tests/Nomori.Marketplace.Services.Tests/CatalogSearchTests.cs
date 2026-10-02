using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Services.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class CatalogSearchTests
{
    private sealed class FakeFacetStore : IProductFacetStore
    {
        public ProductQuery? LastQuery { get; private set; }

        public Task<ProductFacets> GetFacetsAsync(ProductQuery query, int tagLimit, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new ProductFacets(3, 1, 9, [], [], []));
        }
    }

    /// <summary>Only the public tree is used by search; every other member is out of scope here.</summary>
    private sealed class FakeCategoryService(IReadOnlyList<CategoryTreeNode> tree) : ICategoryService
    {
        public Task<IReadOnlyList<CategoryTreeNode>> GetTreeAsync(CancellationToken cancellationToken) => Task.FromResult(tree);
        public Task<Category?> GetAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Category?> GetPublicAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<Category>> GetListAsync(CategoryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CategoryTreeNode>> GetAdminTreeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SelectableCategory>> GetSelectableForSellersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<Category>> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<Category>> UpdateAsync(UpdateCategoryCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<bool>> DeleteAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public FakeProductStore Products { get; } = new();
        public FakeFacetStore Facets { get; } = new();
        public FakeSpecificationStore Specs { get; } = new();

        public Fixture()
        {
            Specs.Options.Add(new SpecificationAttributeOption { Id = 7, SpecificationAttributeId = 70, Name = "Cotton" });
            Specs.Options.Add(new SpecificationAttributeOption { Id = 8, SpecificationAttributeId = 70, Name = "Wool" });
            Specs.Options.Add(new SpecificationAttributeOption { Id = 9, SpecificationAttributeId = 71, Name = "Red" });
        }

        // 1 > (2 > 4), 3
        public CatalogSearchService Create() => new(
            Products, Facets, new FakeCategoryService([
                new CategoryTreeNode { Id = 1, Children = [new CategoryTreeNode { Id = 2, Children = [new CategoryTreeNode { Id = 4 }] }, new CategoryTreeNode { Id = 3 }] }]),
            Specs);

        public ProductQuery Query => Products.LastQuery!;
    }

    private static Task<CatalogResult<PagedResult<Product>>> Search(Fixture f, CatalogSearchRequest request) =>
        f.Create().SearchAsync(request, CancellationToken.None);

    // ---- Pure helpers ----

    [Fact]
    public void TermsAreSplitTrimmedAndLimited()
    {
        Assert.Equal(["blue", "mug"], SearchText.Terms("  blue   mug "));
        Assert.Empty(SearchText.Terms("   "));
        Assert.Equal(SearchLimits.MaxTerms, SearchText.Terms("a b c d e f g").Length);
    }

    [Theory]
    [InlineData("50%", "50[%]")]
    [InlineData("a_b", "a[_]b")]
    [InlineData("[x]", "[[]x]")]
    [InlineData("plain", "plain")]
    public void LikeWildcardsMatchThemselves(string input, string expected) =>
        Assert.Equal(expected, SearchText.EscapeLike(input));

    // ---- Validation ----

    [Theory]
    [InlineData("a")]
    [InlineData(" a ")]
    public async Task ASearchNeedsTwoCharacters(string text)
    {
        var f = new Fixture();

        var result = await Search(f, new CatalogSearchRequest(Search: text));

        Assert.Contains("search", result.Errors.Keys);
        Assert.Null(f.Products.LastQuery);
    }

    [Fact]
    public async Task ATooLongSearchIsRefusedAndABlankOneIsIgnored()
    {
        var f = new Fixture();

        Assert.Contains("search", (await Search(f, new CatalogSearchRequest(Search: new string('a', 101)))).Errors.Keys);
        Assert.True((await Search(f, new CatalogSearchRequest(Search: "   "))).Succeeded);
    }

    [Fact]
    public async Task PriceRangeIsValidated()
    {
        var f = new Fixture();

        Assert.Contains("minPrice", (await Search(f, new CatalogSearchRequest(MinPrice: -1))).Errors.Keys);
        Assert.Contains("maxPrice", (await Search(f, new CatalogSearchRequest(MaxPrice: -1))).Errors.Keys);
        Assert.Contains("maxPrice", (await Search(f, new CatalogSearchRequest(MinPrice: 10, MaxPrice: 5))).Errors.Keys);
        Assert.True((await Search(f, new CatalogSearchRequest(MinPrice: 5, MaxPrice: 5))).Succeeded);
    }

    [Fact]
    public async Task FilterListsAreLimited()
    {
        var f = new Fixture();

        Assert.Contains("manufacturerIds", (await Search(f, new CatalogSearchRequest(ManufacturerIds: Enumerable.Range(1, 21).ToArray()))).Errors.Keys);
        Assert.Contains("tags", (await Search(f, new CatalogSearchRequest(Tags: Enumerable.Range(1, 11).Select(i => "t" + i).ToArray()))).Errors.Keys);
        Assert.Contains("tags", (await Search(f, new CatalogSearchRequest(Tags: [new string('t', 101)]))).Errors.Keys);
        Assert.Contains("specOptionIds", (await Search(f, new CatalogSearchRequest(SpecOptionIds: Enumerable.Range(1, 21).ToArray()))).Errors.Keys);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(-5, 500, 1, 100)]
    [InlineData(3, 20, 3, 20)]
    public async Task PagingIsClamped(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var f = new Fixture();

        await Search(f, new CatalogSearchRequest(Page: page, PageSize: pageSize));

        Assert.Equal((expectedPage, expectedSize), (f.Query.Page, f.Query.PageSize));
    }

    // ---- Public rules ----

    [Fact]
    public async Task EveryQueryIsPublic()
    {
        var f = new Fixture();

        await Search(f, new CatalogSearchRequest());

        Assert.True(f.Query.Published);
        Assert.True(f.Query.OnlyActiveShops);
        Assert.Null(f.Query.Status);
    }

    [Fact]
    public async Task SearchDefaultsToRelevanceAndOtherwiseToTheFeaturedOrder()
    {
        var f = new Fixture();

        await Search(f, new CatalogSearchRequest(Search: "mug"));
        Assert.Equal(ProductSortOrder.Relevance, f.Query.Sort);

        await Search(f, new CatalogSearchRequest());
        Assert.Equal(ProductSortOrder.DisplayOrder, f.Query.Sort);

        await Search(f, new CatalogSearchRequest(Search: "mug", Sort: ProductSortOrder.PriceAsc));
        Assert.Equal(ProductSortOrder.PriceAsc, f.Query.Sort);
    }

    // ---- Category ----

    [Fact]
    public async Task ACategoryIncludesItsDescendants()
    {
        var f = new Fixture();

        await Search(f, new CatalogSearchRequest(CategoryId: 1));
        Assert.Equal([1, 2, 4, 3], f.Query.CategoryIds!);

        await Search(f, new CatalogSearchRequest(CategoryId: 2));
        Assert.Equal([2, 4], f.Query.CategoryIds!);
    }

    [Fact]
    public async Task ACategoryThatIsNotPublicGivesAnEmptyPageWithoutQueryingProducts()
    {
        var f = new Fixture();

        var result = await Search(f, new CatalogSearchRequest(CategoryId: 99, Page: 2, PageSize: 10));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        Assert.Equal((0, 2, 10), (result.Value.TotalCount, result.Value.Page, result.Value.PageSize));
        Assert.Null(f.Products.LastQuery);
    }

    // ---- Filters ----

    [Fact]
    public async Task ManufacturerIdsAreMergedWithTheSingleOne()
    {
        var f = new Fixture();

        await Search(f, new CatalogSearchRequest(ManufacturerIds: [3, 3, 0, 4]));
        Assert.Equal([3, 4], f.Query.ManufacturerIds!);

        await Search(f, new CatalogSearchRequest(ManufacturerIds: []));
        Assert.Null(f.Query.ManufacturerIds!);
    }

    [Fact]
    public async Task TagsAreNormalized()
    {
        var f = new Fixture();

        await Search(f, new CatalogSearchRequest(Tags: [" Gift ", "gift", "SALE", " "]));

        Assert.Equal(["gift", "sale"], f.Query.Tags!);
    }

    [Fact]
    public async Task SpecificationOptionsAreGroupedPerAttribute()
    {
        var f = new Fixture();

        await Search(f, new CatalogSearchRequest(SpecOptionIds: [7, 8, 9, 999]));

        Assert.Equal(2, f.Query.SpecOptionGroups!.Count);
        Assert.Equal([7, 8], f.Query.SpecOptionGroups[0]!);
        Assert.Equal([9], f.Query.SpecOptionGroups[1]!);
    }

    [Fact]
    public async Task InStockPriceAndShopFiltersArePassedOn()
    {
        var f = new Fixture();

        await Search(f, new CatalogSearchRequest(InStock: true, MinPrice: 1, MaxPrice: 9, VendorId: 5));

        Assert.True(f.Query.InStockOnly);
        Assert.Equal((1m, 9m, 5), (f.Query.MinPrice, f.Query.MaxPrice, f.Query.VendorId));
    }

    // ---- Facets ----

    [Fact]
    public async Task FacetsIgnoreTheFacetSelectionsButKeepEveryOtherFilter()
    {
        var f = new Fixture();

        var result = await f.Create().GetFacetsAsync(new CatalogSearchRequest(
            Search: "mug", CategoryId: 2, ManufacturerIds: [3], MinPrice: 1, InStock: true, Tags: ["gift"], SpecOptionIds: [7]), CancellationToken.None);

        Assert.True(result.Succeeded);
        var q = f.Facets.LastQuery!;
        Assert.Null(q.ManufacturerIds);
        Assert.Null(q.Tags);
        Assert.Null(q.SpecOptionGroups);
        Assert.Equal("mug", q.Search);
        Assert.Equal([2, 4], q.CategoryIds!);
        Assert.True(q.InStockOnly && q.Published == true && q.OnlyActiveShops);
        Assert.Equal(1m, q.MinPrice);
    }

    [Fact]
    public async Task FacetsOfAnUnknownCategoryAreEmpty()
    {
        var f = new Fixture();

        var result = await f.Create().GetFacetsAsync(new CatalogSearchRequest(CategoryId: 99), CancellationToken.None);

        Assert.Equal(0, result.Value!.TotalCount);
        Assert.Null(f.Facets.LastQuery);
    }

    [Fact]
    public async Task FacetsValidateTheirInputToo()
    {
        var f = new Fixture();

        var result = await f.Create().GetFacetsAsync(new CatalogSearchRequest(Search: "x"), CancellationToken.None);

        Assert.Contains("search", result.Errors.Keys);
    }

    // ---- Suggestions ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("m")]
    [InlineData(" m ")]
    public async Task ShortTextGivesNoSuggestionsAndNoQuery(string? text)
    {
        var f = new Fixture();

        var result = await f.Create().SuggestAsync(text, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
        Assert.Null(f.Products.LastQuery);
    }

    [Fact]
    public async Task SuggestionsAreAFewPublicProductsByRelevance()
    {
        var f = new Fixture();

        await f.Create().SuggestAsync("  mu ", CancellationToken.None);

        Assert.Equal((1, SearchLimits.SuggestCount, "mu", ProductSortOrder.Relevance), (f.Query.Page, f.Query.PageSize, f.Query.Search, f.Query.Sort));
        Assert.True(f.Query.Published == true && f.Query.OnlyActiveShops);
    }
}
