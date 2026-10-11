using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Seo;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Seo;

/// <summary>
/// Readable URLs, the sitemap and robots.txt (F24-A). The key of an address is <c>slug-id</c>: the id decides what the page is, the slug is
/// computed from the current name, so a renamed product is only a different canonical address and never a broken link.
/// </summary>
public sealed partial class SeoService(
    ISeoStore store,
    ISeoSource source,
    IOptions<SeoOptions> options,
    IClock clock,
    ILogger<SeoService> logger) : ISeoService
{
    private static readonly string[] StaticPaths = ["/storefront", "/storefront/products", "/storefront/vendors"];

    public async Task<SeoPath> ResolvePathAsync(string? type, string? key, CancellationToken cancellationToken)
    {
        var notFound = new SeoPath(false, string.Empty, false);
        if (!SeoRules.TryParseId(key, out var id)) return notFound;

        var name = type switch
        {
            SeoTypes.Product => await source.GetPublicProductNameAsync(id, cancellationToken),
            SeoTypes.Vendor => await source.GetPublicShopNameAsync(id, cancellationToken),
            _ => null
        };
        if (name is null) return notFound;
        var path = SeoRules.PathFor(type!, name, id);
        return new SeoPath(true, path, path.EndsWith("/" + key, StringComparison.Ordinal));
    }

    public async Task<string> BuildSitemapAsync(CancellationToken cancellationToken)
    {
        var baseUrl = options.Value.SiteBaseUrl.TrimEnd('/');
        var now = clock.UtcNow;
        var builder = new StringBuilder();
        var count = 0;

        await using (var writer = new StringWriterUtf8(builder))
        await using (var xml = XmlWriter.Create(writer, new XmlWriterSettings { Async = true, Indent = true, Encoding = Encoding.UTF8 }))
        {
            await xml.WriteStartDocumentAsync();
            await xml.WriteStartElementAsync(null, "urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");

            async Task AddAsync(string path, DateTime? lastModified)
            {
                if (count >= SeoLimits.MaxSitemapUrls) return;
                count++;
                await xml.WriteStartElementAsync(null, "url", null);
                await xml.WriteElementStringAsync(null, "loc", null, baseUrl + path);
                if (lastModified is { } changed) await xml.WriteElementStringAsync(null, "lastmod", null, changed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                await xml.WriteEndElementAsync();
            }

            foreach (var path in StaticPaths) await AddAsync(path, null);

            for (var after = 0; count < SeoLimits.MaxSitemapUrls;)
            {
                var page = await store.ListShopsAsync(after, SeoLimits.SitemapPage, cancellationToken);
                if (page.Count == 0) break;
                foreach (var row in page) await AddAsync(SeoRules.PathFor(SeoTypes.Vendor, row.Name, row.Id), row.UpdatedOnUtc);
                after = page[^1].Id;
            }

            for (var after = 0; count < SeoLimits.MaxSitemapUrls;)
            {
                var page = await store.ListProductsAsync(after, SeoLimits.SitemapPage, now, cancellationToken);
                if (page.Count == 0) break;
                foreach (var row in page) await AddAsync(SeoRules.PathFor(SeoTypes.Product, row.Name, row.Id), row.UpdatedOnUtc);
                after = page[^1].Id;
            }

            if (count >= SeoLimits.MaxSitemapUrls) LogSitemapFull(SeoLimits.MaxSitemapUrls);
            await xml.WriteEndElementAsync();
            await xml.WriteEndDocumentAsync();
        }
        return builder.ToString();
    }

    public string BuildRobots() => SeoRules.Robots(options.Value.SiteBaseUrl);

    /// <summary>A StringWriter that says it is UTF-8, so the XML declaration does not claim UTF-16.</summary>
    private sealed class StringWriterUtf8(StringBuilder builder) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sitemap reached its limit of {Limit} addresses; the rest is left out.")]
    private partial void LogSitemapFull(int limit);
}
