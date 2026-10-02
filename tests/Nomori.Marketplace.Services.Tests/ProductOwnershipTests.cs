using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Catalog;
using Nomori.Marketplace.Services.Vendors;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ProductOwnershipTests
{
    private const int ShopA = 5;
    private const int ShopB = 6;
    private const int Platform = 1;
    private const int Actor = 42;

    private sealed class Fixture
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public FakeTaxonomy Taxonomy { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture()
        {
            Vendors.Vendors.Add(new Vendor { Id = Platform, Name = "Nomori Official", Active = true, IsPlatformShop = true });
            Vendors.Vendors.Add(new Vendor { Id = ShopA, Name = "Shop A", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = ShopB, Name = "Shop B", Active = true });
        }

        public ProductService Create() => new(
            Products, new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors, new NoMembers(), new FakeMediaStore(), Taxonomy, Audit,
            new RecordingEmailSender(), TestOptions.Email(false), NullLog<ProductService>.Instance, new TestClock());
    }

    private static SaveVendorProductCommand Seller(
        string name = "Mug", decimal price = 10, decimal oldPrice = 0, int stock = 3, int[]? categories = null) =>
        new(name, " short ", null, price, oldPrice, stock, categories ?? [1], []);

    private static CreateProductCommand Admin(int? vendorId = null) =>
        new("Plate", null, null, 5, 0, 1, true, vendorId, true, 7, [1], []);

    // ---- Seller isolation ----

    [Fact]
    public async Task SellerCreatesProductsInTheRouteShopWithAdminFieldsAtDefaults()
    {
        var f = new Fixture();
        var result = await f.Create().CreateForVendorAsync(ShopA, Seller(), Actor, CancellationToken.None);

        Assert.True(result.Succeeded);
        var saved = f.Products.Products.Single();
        Assert.Equal(ShopA, saved.VendorId);
        Assert.False(saved.ShowOnHomepage);
        Assert.Equal(0, saved.DisplayOrder);
        Assert.Equal("short", saved.ShortDescription);
        Assert.Equal(TaxonomyAudience.Seller, f.Taxonomy.LastAudience);
        var entry = Assert.Single(f.Audit.Entries, e => e.Event == "product.created");
        Assert.Equal(Actor, entry.CustomerId);
    }

    [Fact]
    public async Task SellerCannotReadUpdateOrDeleteAnotherShopsProduct()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.CreateForVendorAsync(ShopA, Seller(), Actor, CancellationToken.None)).Value!.Id;

        Assert.Null(await service.GetDetailForVendorAsync(ShopB, id, CancellationToken.None));
        Assert.Equal(CatalogErrors.NotFound, (await service.UpdateForVendorAsync(ShopB, id, Seller("Hijack"), Actor, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.DeleteForVendorAsync(ShopB, id, Actor, CancellationToken.None)).ErrorCode);
        Assert.Equal(ShopA, f.Products.Products.Single().VendorId);
        Assert.Equal("Mug", f.Products.Products.Single().Name);
        Assert.False(f.Products.Products.Single().Deleted);

        Assert.NotNull(await service.GetDetailForVendorAsync(ShopA, id, CancellationToken.None));
    }

    [Fact]
    public async Task SellerListIsAlwaysScopedToTheShop()
    {
        var f = new Fixture();
        await f.Create().GetListForVendorAsync(ShopA, new ProductQuery(VendorId: ShopB), CancellationToken.None);

        Assert.Equal(ShopA, f.Products.LastQuery!.VendorId);
    }

    [Fact]
    public async Task SellerUpdateKeepsAdminFieldsAndOwner()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.CreateForVendorAsync(ShopA, Seller(), Actor, CancellationToken.None)).Value!.Id;
        var product = f.Products.Products.Single();
        product.ShowOnHomepage = true;
        product.DisplayOrder = 9;

        var result = await service.UpdateForVendorAsync(ShopA, id, Seller("Renamed", price: 12), Actor, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Renamed", product.Name);
        Assert.True(product.ShowOnHomepage);
        Assert.Equal(9, product.DisplayOrder);
        Assert.Equal(ShopA, product.VendorId);
        Assert.Contains("product.updated", f.Audit.Events);
    }

    [Fact]
    public async Task InactiveShopCannotWriteButCanRead()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.CreateForVendorAsync(ShopA, Seller(), Actor, CancellationToken.None)).Value!.Id;
        f.Vendors.Vendors.Single(v => v.Id == ShopA).Active = false;

        Assert.Equal(CatalogErrors.Forbidden, (await service.CreateForVendorAsync(ShopA, Seller(), Actor, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.Forbidden, (await service.UpdateForVendorAsync(ShopA, id, Seller("X"), Actor, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.Forbidden, (await service.DeleteForVendorAsync(ShopA, id, Actor, CancellationToken.None)).ErrorCode);
        Assert.NotNull(await service.GetDetailForVendorAsync(ShopA, id, CancellationToken.None));
        Assert.Equal(CatalogErrors.NotFound, (await service.CreateForVendorAsync(99, Seller(), Actor, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task SellerDeleteIsSoftAndAudited()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.CreateForVendorAsync(ShopA, Seller(), Actor, CancellationToken.None)).Value!.Id;

        Assert.True((await service.DeleteForVendorAsync(ShopA, id, Actor, CancellationToken.None)).Succeeded);

        Assert.True(f.Products.Products.Single().Deleted);
        Assert.Contains("product.deleted", f.Audit.Events);
        Assert.Equal(CatalogErrors.NotFound, (await service.DeleteForVendorAsync(ShopA, id, Actor, CancellationToken.None)).ErrorCode);
    }

    // ---- Seller validation ----

    [Fact]
    public async Task SellerValidationRules()
    {
        var service = new Fixture().Create();

        Assert.Contains("name", (await service.CreateForVendorAsync(ShopA, Seller(name: " "), Actor, CancellationToken.None)).Errors.Keys);
        Assert.Contains("price", (await service.CreateForVendorAsync(ShopA, Seller(price: 0), Actor, CancellationToken.None)).Errors.Keys);
        Assert.Contains("oldPrice", (await service.CreateForVendorAsync(ShopA, Seller(price: 10, oldPrice: 10), Actor, CancellationToken.None)).Errors.Keys);
        Assert.Contains("stockQuantity", (await service.CreateForVendorAsync(ShopA, Seller(stock: -1), Actor, CancellationToken.None)).Errors.Keys);
        Assert.True((await service.CreateForVendorAsync(ShopA, Seller(price: 10, oldPrice: 12), Actor, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task SellerTaxonomyErrorsAreReported()
    {
        var f = new Fixture();
        f.Taxonomy.Reject = true;

        var result = await f.Create().CreateForVendorAsync(ShopA, Seller(), Actor, CancellationToken.None);

        Assert.Contains("categoryIds", result.Errors.Keys);
        Assert.Empty(f.Products.Products);
    }

    // ---- Administrators ----

    [Fact]
    public async Task AdminCreateDefaultsToThePlatformShopAndChecksExplicitShops()
    {
        var f = new Fixture();
        var service = f.Create();

        Assert.Equal(Platform, (await service.CreateAsync(Admin(), Actor, CancellationToken.None)).Value!.VendorId);
        Assert.Equal(ShopB, (await service.CreateAsync(Admin(ShopB), Actor, CancellationToken.None)).Value!.VendorId);
        Assert.Contains("vendorId", (await service.CreateAsync(Admin(99), Actor, CancellationToken.None)).Errors.Keys);

        f.Vendors.Vendors.Single(v => v.Id == ShopA).Deleted = true;
        Assert.Contains("vendorId", (await service.CreateAsync(Admin(ShopA), Actor, CancellationToken.None)).Errors.Keys);
        Assert.Equal(TaxonomyAudience.Admin, f.Taxonomy.LastAudience);
    }

    [Fact]
    public async Task AdminUpdateKeepsTheOwnerAndRejectsDifferentOnes()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.CreateAsync(Admin(ShopA), Actor, CancellationToken.None)).Value!.Id;

        UpdateProductCommand Update(int? vendorId) =>
            new(id, "Plate 2", null, null, 6, 0, 2, true, vendorId, true, 1, [1], []);

        // This is the bug fixed in F10-A: the admin form never sent a vendor, which used to clear it.
        Assert.True((await service.UpdateAsync(Update(null), Actor, CancellationToken.None)).Succeeded);
        Assert.Equal(ShopA, f.Products.Products.Single().VendorId);
        Assert.True((await service.UpdateAsync(Update(ShopA), Actor, CancellationToken.None)).Succeeded);

        var moved = await service.UpdateAsync(Update(ShopB), Actor, CancellationToken.None);
        Assert.Contains("vendorId", moved.Errors.Keys);
        Assert.Equal(ShopA, f.Products.Products.Single().VendorId);
        Assert.Equal(CatalogErrors.NotFound, (await service.UpdateAsync(Update(null) with { Id = 999 }, Actor, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task TransferMovesTheProductAndAudits()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = (await service.CreateAsync(Admin(ShopA), Actor, CancellationToken.None)).Value!.Id;

        Assert.True((await service.TransferAsync(id, ShopB, Actor, CancellationToken.None)).Succeeded);
        Assert.Equal(ShopB, f.Products.Products.Single().VendorId);
        Assert.Contains("product.transferred", f.Audit.Events);

        Assert.Contains("vendorId", (await service.TransferAsync(id, ShopB, Actor, CancellationToken.None)).Errors.Keys);
        Assert.Contains("vendorId", (await service.TransferAsync(id, 99, Actor, CancellationToken.None)).Errors.Keys);
        Assert.Equal(CatalogErrors.NotFound, (await service.TransferAsync(999, ShopA, Actor, CancellationToken.None)).ErrorCode);
    }

    // ---- Platform shop ----

    [Fact]
    public async Task PlatformShopCannotBeDeletedOrDeactivated()
    {
        var f = new Fixture();
        var service = new VendorService(f.Vendors, new FakeMediaStore(), f.Audit, new TestClock());

        Assert.Equal(VendorErrors.PlatformShop, (await service.DeleteAsync(Platform, Actor, CancellationToken.None)).ErrorCode);
        Assert.False(f.Vendors.DeleteCalled);

        var deactivate = await service.UpdateAsync(new UpdateVendorCommand(Platform, "Nomori Official", "p@example.com", null, null, false, 0), CancellationToken.None);
        Assert.Contains("active", deactivate.Errors.Keys);
        Assert.True((await service.UpdateAsync(new UpdateVendorCommand(Platform, "Nomori Official", "p@example.com", null, null, true, 0), CancellationToken.None)).Succeeded);
    }

    // ---- Fakes ----

    internal sealed class FakeTaxonomy : ITaxonomyService
    {
        public bool Reject { get; set; }
        public TaxonomyAudience? LastAudience { get; private set; }

        public Task<CatalogResult<TaxonomySelection>> ValidateSelectionAsync(
            int[] categoryIds, int[] manufacturerIds, TaxonomyAudience audience, CancellationToken cancellationToken)
        {
            LastAudience = audience;
            return Task.FromResult(Reject
                ? CatalogResult.Failure<TaxonomySelection>("categoryIds", "Category 1 cannot be used by sellers.")
                : CatalogResult.Success(new TaxonomySelection(categoryIds.Distinct().ToArray(), manufacturerIds.Distinct().ToArray())));
        }
    }

    internal sealed class FakeProductStore : IProductStore
    {
        private int nextId = 1;

        public List<Product> Products { get; } = [];
        public ProductQuery? LastQuery { get; private set; }
        private readonly Dictionary<int, int[]> categories = [];
        private readonly Dictionary<int, int[]> pictures = [];

        /// <summary>When true, a product without stored pictures reports one, so tests that are not about pictures can publish.</summary>
        public bool DefaultPicture { get; set; }

        public Task<Product?> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(Products.FirstOrDefault(p => p.Id == id && !p.Deleted));

        public Task<(IReadOnlyList<Product> Items, int TotalCount)> GetPagedAsync(ProductQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult<(IReadOnlyList<Product>, int)>(([], 0));
        }

        public Task<IReadOnlyList<int>> GetCategoryIdsAsync(int productId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(categories.GetValueOrDefault(productId) ?? []);

        public int[] CategoriesOf(int productId) => categories.GetValueOrDefault(productId) ?? [];

        public Task<IReadOnlyList<int>> GetManufacturerIdsAsync(int productId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>([]);

        public Task<int> InsertAsync(Product product, CancellationToken cancellationToken)
        {
            product.Id = nextId++;
            Products.Add(product);
            return Task.FromResult(product.Id);
        }

        public Task UpdateAsync(Product product, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(int id, CancellationToken cancellationToken)
        {
            Products.Single(p => p.Id == id).Deleted = true;
            return Task.CompletedTask;
        }

        public Task UpdateLifecycleAsync(Product product, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetVendorAsync(int productId, int vendorId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            Products.Single(p => p.Id == productId).VendorId = vendorId;
            return Task.CompletedTask;
        }

        public Task SetCategoriesAsync(int productId, int[] categoryIds, CancellationToken cancellationToken)
        {
            categories[productId] = categoryIds;
            return Task.CompletedTask;
        }

        public Task SetManufacturersAsync(int productId, int[] manufacturerIds, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<int>> GetPictureIdsAsync(int productId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(pictures.TryGetValue(productId, out var ids) ? ids : DefaultPicture ? [9000 + productId] : []);

        public Task<IReadOnlyDictionary<int, int>> GetMainPictureIdsAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(productIds.Where(pictures.ContainsKey).ToDictionary(id => id, id => pictures[id].FirstOrDefault()));

        public Task<int?> GetPictureOwnerAsync(int mediaAssetId, CancellationToken cancellationToken) =>
            Task.FromResult(pictures.Where(kv => kv.Value.Contains(mediaAssetId)).Select(kv => (int?)kv.Key).FirstOrDefault());

        public Task SetPicturesAsync(int productId, int[] mediaAssetIds, CancellationToken cancellationToken)
        {
            pictures[productId] = mediaAssetIds;
            return Task.CompletedTask;
        }

        private readonly Dictionary<int, int[]> related = [];
        private readonly Dictionary<int, int[]> manufacturers = [];

        public Task UpdateContentAsync(Product product, CancellationToken cancellationToken) => Task.CompletedTask;

        public HashSet<int> ProductsWithVariants { get; } = [];

        public Task<bool> HasVariantsAsync(int productId, CancellationToken cancellationToken) =>
            Task.FromResult(ProductsWithVariants.Contains(productId));

        public Task<bool> IsSkuTakenAsync(int vendorId, string sku, int excludeProductId, CancellationToken cancellationToken) =>
            Task.FromResult(Products.Any(p => !p.Deleted && p.VendorId == vendorId && p.Id != excludeProductId
                && string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<int>> GetRelatedIdsAsync(int productId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(related.GetValueOrDefault(productId) ?? []);

        public Task SetRelatedAsync(int productId, int[] relatedProductIds, CancellationToken cancellationToken)
        {
            related[productId] = relatedProductIds;
            return Task.CompletedTask;
        }
    }

    internal sealed class NoMembers : IVendorMemberStore
    {
        public List<VendorMember> Members { get; } = [];

        public Task<IReadOnlyList<VendorMember>> ListAsync(int vendorId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VendorMember>>(Members.ToList());

        public Task<VendorMember?> GetAsync(int vendorId, int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CreateMemberStoreResult> CreateAsync(int vendorId, string email, string? firstName, string? lastName, string passwordHash,
            string passwordSalt, string setupTokenHash, DateTime setupTokenExpiresOnUtc, int maxMembers, DateTime nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReplaceSetupTokenAsync(int customerId, string tokenHash, DateTime expiresOnUtc, DateTime nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RemoveMemberOutcome> RemoveAsync(int vendorId, int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class UnusedCategoryStore : ICategoryStore
    {
        public Task<Category?> GetAsync(int id, CancellationToken cancellationToken) => Task.FromResult<Category?>(null);
        public Task<(IReadOnlyList<Category> Items, int TotalCount)> GetPagedAsync(CategoryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Category>> GetAllPublishedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountChildrenAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountProductsAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> NameExistsAsync(string name, int parentCategoryId, int? excludeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> InsertAsync(Category category, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateAsync(Category category, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class UnusedManufacturerStore : IManufacturerStore
    {
        public Task<Manufacturer?> GetAsync(int id, CancellationToken cancellationToken) => Task.FromResult<Manufacturer?>(null);
        public Task<(IReadOnlyList<Manufacturer> Items, int TotalCount)> GetPagedAsync(ManufacturerQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Manufacturer>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountProductsAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> NameExistsAsync(string name, int? excludeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> InsertAsync(Manufacturer manufacturer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateAsync(Manufacturer manufacturer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
