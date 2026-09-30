using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Services.Catalog;

namespace Nomori.Marketplace.Services.Tests;

public sealed class TaxonomyServiceTests
{
    private sealed class Fixture
    {
        public FakeCategoryStore Categories { get; } = new();
        public FakeManufacturerStore Manufacturers { get; } = new();

        public CategoryService Category() => new(Categories, new FakeMediaStore(), new TestClock());
        public ManufacturerService Manufacturer() => new(Manufacturers, new FakeMediaStore(), new TestClock());
        public TaxonomyService Taxonomy() => new(Categories, Manufacturers);

        /// <summary>Fashion(1) > Women(2) > Dresses(3); Electronics(4) published; Hidden(5) unpublished with child Secret(6).</summary>
        public Fixture Seed()
        {
            Categories.Add(1, "Fashion", 0);
            Categories.Add(2, "Women", 1);
            Categories.Add(3, "Dresses", 2);
            Categories.Add(4, "Electronics", 0);
            Categories.Add(5, "Hidden", 0, published: false);
            Categories.Add(6, "Secret", 5);
            return this;
        }
    }

    private static CreateCategoryCommand NewCategory(string name, int parent = 0) =>
        new(name, null, parent, 0, false, true, 0);

    private static UpdateCategoryCommand Update(Category c, string? name = null, int? parent = null, bool? restrict = null) =>
        new(c.Id, name ?? c.Name, c.Description, parent ?? c.ParentCategoryId, c.PictureId, c.ShowOnHomepage, c.Published, c.DisplayOrder, restrict ?? c.RestrictFromVendors);

    // ---- Tree integrity ----

    [Fact]
    public async Task CreateRejectsMissingParentAndDuplicateSiblingNames()
    {
        var f = new Fixture().Seed();
        var service = f.Category();

        Assert.Contains("parentCategoryId", (await service.CreateAsync(NewCategory("X", 999), CancellationToken.None)).Errors.Keys);
        Assert.Contains("parentCategoryId", (await service.CreateAsync(NewCategory("X", -1), CancellationToken.None)).Errors.Keys);
        Assert.Contains("name", (await service.CreateAsync(NewCategory("women", 1), CancellationToken.None)).Errors.Keys);
        Assert.True((await service.CreateAsync(NewCategory("Women", 4), CancellationToken.None)).Succeeded);
        Assert.True((await service.CreateAsync(NewCategory("Shoes", 1), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task UpdateRejectsSelfAndDescendantParents()
    {
        var f = new Fixture().Seed();
        var service = f.Category();
        var fashion = f.Categories.Get(1);

        Assert.Contains("parentCategoryId", (await service.UpdateAsync(Update(fashion, parent: 1), CancellationToken.None)).Errors.Keys);
        Assert.Contains("parentCategoryId", (await service.UpdateAsync(Update(fashion, parent: 3), CancellationToken.None)).Errors.Keys);
        Assert.True((await service.UpdateAsync(Update(f.Categories.Get(3), parent: 4), CancellationToken.None)).Succeeded);
        Assert.Equal(4, f.Categories.Get(3).ParentCategoryId);
    }

    [Fact]
    public async Task UpdateChecksSiblingNamesOnlyWhenNameOrParentChanges()
    {
        var f = new Fixture().Seed();
        f.Categories.Add(7, "Women", 4);
        var service = f.Category();

        Assert.Contains("name", (await service.UpdateAsync(Update(f.Categories.Get(7), parent: 1), CancellationToken.None)).Errors.Keys);
        Assert.True((await service.UpdateAsync(Update(f.Categories.Get(7), restrict: true), CancellationToken.None)).Succeeded);
        Assert.True(f.Categories.Get(7).RestrictFromVendors);
        Assert.Equal(CatalogErrors.NotFound, (await service.UpdateAsync(Update(f.Categories.Get(7)) with { Id = 999 }, CancellationToken.None)).ErrorCode);
    }

    // ---- Delete rules ----

    [Fact]
    public async Task DeleteRefusesCategoriesWithChildrenOrProducts()
    {
        var f = new Fixture().Seed();
        f.Categories.ProductCounts[3] = 2;
        var service = f.Category();

        Assert.Equal(CatalogErrors.CategoryHasChildren, (await service.DeleteAsync(1, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.CategoryInUse, (await service.DeleteAsync(3, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.DeleteAsync(999, CancellationToken.None)).ErrorCode);
        Assert.True((await service.DeleteAsync(4, CancellationToken.None)).Succeeded);
        Assert.DoesNotContain(f.Categories.All, c => c.Id == 4);
    }

    [Fact]
    public async Task ManufacturerDeleteAndNameRules()
    {
        var f = new Fixture();
        f.Manufacturers.Add(1, "Acme");
        f.Manufacturers.Add(2, "Globex");
        f.Manufacturers.ProductCounts[1] = 1;
        var service = f.Manufacturer();

        Assert.Equal(CatalogErrors.ManufacturerInUse, (await service.DeleteAsync(1, CancellationToken.None)).ErrorCode);
        Assert.True((await service.DeleteAsync(2, CancellationToken.None)).Succeeded);
        Assert.Contains("name", (await service.CreateAsync(new CreateManufacturerCommand(" ACME ", null, 0, true, 0), CancellationToken.None)).Errors.Keys);
        Assert.True((await service.CreateAsync(new CreateManufacturerCommand("Initech", null, 0, true, 0), CancellationToken.None)).Succeeded);
    }

    // ---- Selection ----

    [Fact]
    public async Task AdminSelectionNeedsExistingRecordsButAllowsUnpublished()
    {
        var f = new Fixture().Seed();
        f.Manufacturers.Add(1, "Acme");

        var ok = await f.Taxonomy().ValidateSelectionAsync([1, 1, 5], [1, 1], TaxonomyAudience.Admin, CancellationToken.None);
        Assert.True(ok.Succeeded);
        Assert.Equal([1, 5], ok.Value!.CategoryIds);
        Assert.Equal([1], ok.Value.ManufacturerIds);

        var bad = await f.Taxonomy().ValidateSelectionAsync([1, 999, 0, -4], [77], TaxonomyAudience.Admin, CancellationToken.None);
        Assert.Contains("categoryIds", bad.Errors.Keys);
        Assert.Contains("manufacturerIds", bad.Errors.Keys);
    }

    [Fact]
    public async Task SellerSelectionRejectsRestrictedHiddenAndUnpublished()
    {
        var f = new Fixture().Seed();
        f.Categories.Get(3).RestrictFromVendors = true;
        f.Manufacturers.Add(1, "Acme");
        f.Manufacturers.Add(2, "Draft", published: false);
        var taxonomy = f.Taxonomy();

        Assert.True((await taxonomy.ValidateSelectionAsync([2, 4], [1], TaxonomyAudience.Seller, CancellationToken.None)).Succeeded);
        Assert.Contains("categoryIds", (await taxonomy.ValidateSelectionAsync([3], [], TaxonomyAudience.Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("categoryIds", (await taxonomy.ValidateSelectionAsync([5], [], TaxonomyAudience.Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("categoryIds", (await taxonomy.ValidateSelectionAsync([6], [], TaxonomyAudience.Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("manufacturerIds", (await taxonomy.ValidateSelectionAsync([], [2], TaxonomyAudience.Seller, CancellationToken.None)).Errors.Keys);
        Assert.True((await taxonomy.ValidateSelectionAsync([3, 6], [2], TaxonomyAudience.Admin, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task SelectionIsLimited()
    {
        var f = new Fixture();
        for (var i = 1; i <= 11; i++) f.Categories.Add(i, $"C{i}", 0);

        var tooMany = await f.Taxonomy().ValidateSelectionAsync(Enumerable.Range(1, 11).ToArray(), [], TaxonomyAudience.Admin, CancellationToken.None);
        Assert.Contains("categoryIds", tooMany.Errors.Keys);
        Assert.True((await f.Taxonomy().ValidateSelectionAsync(Enumerable.Range(1, 10).ToArray(), [], TaxonomyAudience.Admin, CancellationToken.None)).Succeeded);
    }

    // ---- Visibility ----

    [Fact]
    public async Task PublicVisibilityRequiresEveryAncestorPublished()
    {
        var f = new Fixture().Seed();
        var service = f.Category();

        Assert.NotNull(await service.GetPublicAsync(3, CancellationToken.None));
        Assert.Null(await service.GetPublicAsync(5, CancellationToken.None));
        Assert.Null(await service.GetPublicAsync(6, CancellationToken.None));
        Assert.Null(await service.GetPublicAsync(999, CancellationToken.None));

        f.Categories.Get(1).Published = false;
        Assert.Null(await service.GetPublicAsync(3, CancellationToken.None));
    }

    [Fact]
    public async Task SelectableListHasPathsAndExcludesRestrictedAndHidden()
    {
        var f = new Fixture().Seed();
        f.Categories.Get(2).RestrictFromVendors = true;

        var selectable = await f.Category().GetSelectableForSellersAsync(CancellationToken.None);

        Assert.Equal([1, 3, 4], selectable.Select(c => c.Id).OrderBy(i => i).ToArray());
        Assert.Equal("Fashion > Women > Dresses", selectable.Single(c => c.Id == 3).Path);
    }

    [Fact]
    public async Task AdminTreeIncludesUnpublishedAndPublicTreeDoesNot()
    {
        var f = new Fixture().Seed();
        var service = f.Category();

        var admin = await service.GetAdminTreeAsync(CancellationToken.None);
        Assert.Contains(admin, n => n.Id == 5 && !n.Published && n.Children.Any(c => c.Id == 6));

        var publicTree = await service.GetTreeAsync(CancellationToken.None);
        Assert.DoesNotContain(publicTree, n => n.Id == 5);
    }

    // ---- Fakes ----

    private sealed class FakeCategoryStore : ICategoryStore
    {
        private readonly List<Category> categories = [];
        public IReadOnlyList<Category> All => categories;
        public Dictionary<int, int> ProductCounts { get; } = [];

        public void Add(int id, string name, int parent, bool published = true) =>
            categories.Add(new Category { Id = id, Name = name, ParentCategoryId = parent, Published = published });

        public Category Get(int id) => categories.Single(c => c.Id == id);

        public Task<Category?> GetAsync(int id, CancellationToken cancellationToken) => Task.FromResult(categories.FirstOrDefault(c => c.Id == id));
        public Task<(IReadOnlyList<Category> Items, int TotalCount)> GetPagedAsync(CategoryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Category>> GetAllPublishedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Category>>(categories.Where(c => c.Published).ToList());
        public Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Category>>(categories.ToList());
        public Task<int> CountChildrenAsync(int id, CancellationToken cancellationToken) => Task.FromResult(categories.Count(c => c.ParentCategoryId == id));
        public Task<int> CountProductsAsync(int id, CancellationToken cancellationToken) => Task.FromResult(ProductCounts.GetValueOrDefault(id));

        public Task<bool> NameExistsAsync(string name, int parentCategoryId, int? excludeId, CancellationToken cancellationToken) =>
            Task.FromResult(categories.Any(c => c.ParentCategoryId == parentCategoryId && c.Id != excludeId
                                                && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<int> InsertAsync(Category category, CancellationToken cancellationToken)
        {
            category.Id = categories.Count == 0 ? 1 : categories.Max(c => c.Id) + 1;
            categories.Add(category);
            return Task.FromResult(category.Id);
        }

        public Task UpdateAsync(Category category, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(int id, CancellationToken cancellationToken)
        {
            categories.RemoveAll(c => c.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeManufacturerStore : IManufacturerStore
    {
        private readonly List<Manufacturer> manufacturers = [];
        public Dictionary<int, int> ProductCounts { get; } = [];

        public void Add(int id, string name, bool published = true) =>
            manufacturers.Add(new Manufacturer { Id = id, Name = name, Published = published });

        public Task<Manufacturer?> GetAsync(int id, CancellationToken cancellationToken) => Task.FromResult(manufacturers.FirstOrDefault(m => m.Id == id));
        public Task<(IReadOnlyList<Manufacturer> Items, int TotalCount)> GetPagedAsync(ManufacturerQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Manufacturer>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Manufacturer>>(manufacturers.Where(m => ids.Contains(m.Id)).ToList());

        public Task<int> CountProductsAsync(int id, CancellationToken cancellationToken) => Task.FromResult(ProductCounts.GetValueOrDefault(id));

        public Task<bool> NameExistsAsync(string name, int? excludeId, CancellationToken cancellationToken) =>
            Task.FromResult(manufacturers.Any(m => m.Id != excludeId && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<int> InsertAsync(Manufacturer manufacturer, CancellationToken cancellationToken)
        {
            manufacturer.Id = manufacturers.Count == 0 ? 1 : manufacturers.Max(m => m.Id) + 1;
            manufacturers.Add(manufacturer);
            return Task.FromResult(manufacturer.Id);
        }

        public Task UpdateAsync(Manufacturer manufacturer, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(int id, CancellationToken cancellationToken)
        {
            manufacturers.RemoveAll(m => m.Id == id);
            return Task.CompletedTask;
        }
    }
}
