using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Seo;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Seo;

namespace Nomori.Marketplace.Services.Tests;

public sealed class SeoTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class FakeSource : ISeoSource
    {
        public Dictionary<int, string> Products { get; } = [];
        public Dictionary<int, string> Shops { get; } = [];

        public Task<string?> GetPublicProductNameAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Products.GetValueOrDefault(id));
        public Task<string?> GetPublicShopNameAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Shops.GetValueOrDefault(id));
    }

    private sealed class FakeStore : ISeoStore
    {
        public List<SitemapEntry> Products { get; } = [];
        public List<SitemapEntry> Shops { get; } = [];

        public Task<IReadOnlyList<SitemapEntry>> ListProductsAsync(int afterId, int take, DateTime nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SitemapEntry>>(Products.Where(p => p.Id > afterId).OrderBy(p => p.Id).Take(take).ToList());

        public Task<IReadOnlyList<SitemapEntry>> ListShopsAsync(int afterId, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SitemapEntry>>(Shops.Where(p => p.Id > afterId).OrderBy(p => p.Id).Take(take).ToList());
    }

    private static SeoService Create(FakeStore? store = null, FakeSource? source = null, string baseUrl = "https://nomori.test/") =>
        new(store ?? new FakeStore(), source ?? new FakeSource(), Options.Create(new SeoOptions { SiteBaseUrl = baseUrl }), new FixedClock(), NullLogger<SeoService>.Instance);

    // ---- Slug ----

    [Theory]
    [InlineData("White Ceramic Vase", "white-ceramic-vase")]
    [InlineData("  Hello,   World!!  ", "hello-world")]
    [InlineData("Bình gốm men trắng", "binh-gom-men-trang")]
    [InlineData("Đèn Đẹp Đà Nẵng", "den-dep-da-nang")]
    [InlineData("Café Crème 100% (new)", "cafe-creme-100-new")]
    [InlineData("A--B__C", "a-b-c")]
    [InlineData("2024", "2024")]
    public void ANameBecomesALowerCaseSlugWithoutAccentsOrSymbols(string name, string slug) =>
        Assert.Equal(slug, SeoRules.Slugify(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("花瓶")]
    public void ANameWithoutLettersOrDigitsGivesNoSlug(string? name) => Assert.Equal(string.Empty, SeoRules.Slugify(name));

    [Fact]
    public void ALongNameIsCutAtAWordAndAWordLongerThanTheLimitIsCutHard()
    {
        var slug = SeoRules.Slugify(string.Join(' ', Enumerable.Repeat("elegant", 20)));

        Assert.True(slug.Length <= SeoLimits.MaxSlugLength);
        Assert.False(slug.EndsWith('-'));
        Assert.All(slug.Split('-'), word => Assert.Equal("elegant", word));
        Assert.Equal(SeoLimits.MaxSlugLength, SeoRules.Slugify(new string('x', 200)).Length);
    }

    // ---- Key ----

    [Fact]
    public void TheKeyIsTheSlugAndTheIdOrJustTheId()
    {
        Assert.Equal("white-vase-57", SeoRules.KeyFor("White Vase", 57));
        Assert.Equal("57", SeoRules.KeyFor("!!!", 57));
        Assert.Equal("/storefront/vendors/mugs-inc-3", SeoRules.PathFor(SeoTypes.Vendor, "Mugs Inc", 3));
        Assert.Equal("/storefront/products/mug-3", SeoRules.PathFor(SeoTypes.Product, "Mug", 3));
    }

    [Theory]
    [InlineData("57", 57)]
    [InlineData("white-vase-57", 57)]
    [InlineData("2024-vase-9", 9)]
    [InlineData("a-b-c-1000", 1000)]
    public void TheIdIsWhatTheKeyEndsWith(string key, int id)
    {
        Assert.True(SeoRules.TryParseId(key, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("white-vase")]
    [InlineData("white-vase-")]
    [InlineData("0")]
    [InlineData("vase-0")]
    [InlineData("vase-99999999999")]
    [InlineData("vase-1.5")]
    [InlineData("vase 57")]
    [InlineData("v%20-57")]
    [InlineData("vase-57/../x")]
    public void AKeyWithoutAValidIdAtTheEndOrWithOtherCharactersIsRefused(string? key) => Assert.False(SeoRules.TryParseId(key, out _));

    [Fact]
    public void AKeyThatIsTooLongIsRefused() => Assert.False(SeoRules.TryParseId(new string('a', 250) + "-5", out _));

    // ---- Description ----

    [Fact]
    public void ADescriptionIsPlainTextCutAtAWord()
    {
        Assert.Equal("Fine ceramic vase. Hand made.", SeoRules.Describe("<p>Fine   ceramic <b>vase</b>.</p>\n<p>Hand made.</p>"));
        Assert.Equal(string.Empty, SeoRules.Describe("  "));

        var text = SeoRules.Describe(string.Join(' ', Enumerable.Repeat("lovely", 60)));
        Assert.True(text.Length <= SeoLimits.MaxMetaDescription);
        Assert.EndsWith("…", text);
        Assert.DoesNotContain("lov…", text, StringComparison.Ordinal);
    }

    // ---- Canonical path ----

    [Fact]
    public async Task ACanonicalKeyIsFoundAndCanonical()
    {
        var source = new FakeSource { Products = { [57] = "White Vase" } };

        var path = await Create(source: source).ResolvePathAsync("product", "white-vase-57", CancellationToken.None);

        Assert.Equal(new SeoPath(true, "/storefront/products/white-vase-57", true), path);
    }

    [Theory]
    [InlineData("57")]
    [InlineData("old-name-57")]
    [InlineData("White-Vase-57")]
    public async Task AnOldSlugABareIdOrAnotherCaseIsFoundButNotCanonical(string key)
    {
        var source = new FakeSource { Products = { [57] = "White Vase" } };

        var path = await Create(source: source).ResolvePathAsync("product", key, CancellationToken.None);

        Assert.True(path.Found);
        Assert.False(path.IsCanonical);
        Assert.Equal("/storefront/products/white-vase-57", path.CanonicalPath);
    }

    [Fact]
    public async Task AShopAndAProductWithTheSameIdAreDifferentPages()
    {
        var source = new FakeSource { Products = { [3] = "Mug" }, Shops = { [3] = "Mugs Inc" } };
        var service = Create(source: source);

        Assert.Equal("/storefront/vendors/mugs-inc-3", (await service.ResolvePathAsync("vendor", "3", CancellationToken.None)).CanonicalPath);
        Assert.Equal("/storefront/products/mug-3", (await service.ResolvePathAsync("product", "3", CancellationToken.None)).CanonicalPath);
    }

    [Fact]
    public async Task SomethingNotPublicABadKeyOrAnUnknownTypeIsNotFound()
    {
        var service = Create(source: new FakeSource { Products = { [1] = "Mug" } });

        Assert.False((await service.ResolvePathAsync("product", "2", CancellationToken.None)).Found);
        Assert.False((await service.ResolvePathAsync("product", "mug", CancellationToken.None)).Found);
        Assert.False((await service.ResolvePathAsync("vendor", "1", CancellationToken.None)).Found);
        Assert.False((await service.ResolvePathAsync("category", "1", CancellationToken.None)).Found);
        Assert.False((await service.ResolvePathAsync(null, "1", CancellationToken.None)).Found);
    }

    // ---- Sitemap ----

    [Fact]
    public async Task TheSitemapListsThePagesTheShopsAndTheProductsWithTheBaseAddress()
    {
        var store = new FakeStore();
        store.Shops.Add(new SitemapEntry(3, "Mugs & Co", new DateTime(2026, 2, 1)));
        store.Products.Add(new SitemapEntry(57, "White Vase", new DateTime(2026, 3, 9)));

        var xml = XDocument.Parse(await Create(store).BuildSitemapAsync(CancellationToken.None));

        var locs = xml.Root!.Elements(Ns + "url").Select(u => u.Element(Ns + "loc")!.Value).ToList();
        Assert.Equal(
        [
            "https://nomori.test/storefront", "https://nomori.test/storefront/products", "https://nomori.test/storefront/vendors",
            "https://nomori.test/storefront/vendors/mugs-co-3", "https://nomori.test/storefront/products/white-vase-57"
        ], locs);
        Assert.Equal("2026-03-09", xml.Root.Elements(Ns + "url").Last().Element(Ns + "lastmod")!.Value);
    }

    [Fact]
    public async Task TheSitemapPagesThroughMoreRowsThanOnePage()
    {
        var store = new FakeStore();
        for (var i = 1; i <= SeoLimits.SitemapPage + 5; i++) store.Products.Add(new SitemapEntry(i, $"P{i}", Now));

        var xml = XDocument.Parse(await Create(store).BuildSitemapAsync(CancellationToken.None));

        Assert.Equal(3 + SeoLimits.SitemapPage + 5, xml.Root!.Elements(Ns + "url").Count());
    }

    [Fact]
    public async Task TheSitemapStopsAtTheProtocolLimit()
    {
        var store = new FakeStore();
        for (var i = 1; i <= SeoLimits.MaxSitemapUrls + 100; i++) store.Products.Add(new SitemapEntry(i, "P", Now));

        var xml = XDocument.Parse(await Create(store).BuildSitemapAsync(CancellationToken.None));

        Assert.Equal(SeoLimits.MaxSitemapUrls, xml.Root!.Elements(Ns + "url").Count());
    }

    [Fact]
    public async Task TheSitemapDeclaresUtf8()
    {
        var text = await Create().BuildSitemapAsync(CancellationToken.None);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Robots ----

    [Fact]
    public void RobotsKeepsCrawlersOutOfThePrivateAreasAndPointsToTheSitemap()
    {
        var robots = Create(baseUrl: "https://nomori.test/").BuildRobots();

        Assert.Contains("User-agent: *", robots);
        foreach (var area in new[] { "/admin", "/vendor", "/customer", "/auth", "/storefront/cart", "/storefront/checkout", "/payments" })
            Assert.Contains($"Disallow: {area}", robots);
        Assert.DoesNotContain("Disallow: /storefront/products", robots);
        Assert.Contains("Sitemap: https://nomori.test/sitemap.xml", robots);
    }
}
