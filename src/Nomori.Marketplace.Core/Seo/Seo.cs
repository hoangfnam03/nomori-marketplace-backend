using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nomori.Marketplace.Core.Seo;

/// <summary>Settings of the public site address (section <c>Seo</c>).</summary>
public sealed class SeoOptions
{
    public const string SectionName = "Seo";

    /// <summary>The public address of the storefront, used in the sitemap and in robots.txt. No trailing slash needed.</summary>
    public string SiteBaseUrl { get; set; } = "http://localhost:4200";
}

public static class SeoLimits
{
    public const int MaxSlugLength = 80;
    public const int MaxKeyLength = 200;
    public const int MaxMetaDescription = 160;

    /// <summary>The sitemap protocol allows 50,000 addresses in one file.</summary>
    public const int MaxSitemapUrls = 50_000;

    public const int SitemapPage = 1_000;
}

public static class SeoTypes
{
    public const string Product = "product";
    public const string Vendor = "vendor";
}

/// <param name="Found">False when the key is not a valid key or what it points to is not public.</param>
/// <param name="CanonicalPath">The address the page should be on; empty when not found.</param>
/// <param name="IsCanonical">True when the key asked for is already the canonical one.</param>
public sealed record SeoPath(bool Found, string CanonicalPath, bool IsCanonical);

/// <summary>A row of the sitemap: something public, its name and when it last changed.</summary>
public sealed record SitemapEntry(int Id, string Name, DateTime UpdatedOnUtc);

public static partial class SeoRules
{
    public const string ProductsPath = "/storefront/products";
    public const string VendorsPath = "/storefront/vendors";

    /// <summary>
    /// A name as a URL fragment: lower case, accents removed (including d with stroke), letters and digits only, words joined by one dash,
    /// at most 80 characters cut at a word. A name without a letter or a digit gives an empty string.
    /// </summary>
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var builder = new StringBuilder(name.Length);
        var pendingDash = false;
        foreach (var raw in name.Normalize(NormalizationForm.FormD))
        {
            var c = raw switch { 'đ' or 'Đ' => 'd', _ => raw };
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0) builder.Append('-');
                pendingDash = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = builder.ToString();
        if (slug.Length <= SeoLimits.MaxSlugLength) return slug;

        var cut = slug[..SeoLimits.MaxSlugLength];
        // Cut at a word, unless the first word alone is that long.
        var lastDash = cut.LastIndexOf('-');
        return lastDash > 0 ? cut[..lastDash] : cut;
    }

    /// <summary>The key of an address: the slug and the id, or just the id when the name gives no slug.</summary>
    public static string KeyFor(string? name, int id)
    {
        var slug = Slugify(name);
        var number = id.ToString(CultureInfo.InvariantCulture);
        return slug.Length == 0 ? number : $"{slug}-{number}";
    }

    public static string PathFor(string type, string? name, int id) =>
        (type == SeoTypes.Vendor ? VendorsPath : ProductsPath) + "/" + KeyFor(name, id);

    /// <summary>
    /// The id a key ends with. Any slug is accepted in front of it (an old name still reaches the page); a key with other characters
    /// than letters, digits and dashes, or without an id at the end, is refused.
    /// </summary>
    public static bool TryParseId(string? key, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(key) || key.Length > SeoLimits.MaxKeyLength || !KeyPattern().IsMatch(key)) return false;
        var tail = key[(key.LastIndexOf('-') + 1)..];
        return tail.Length is > 0 and <= 9 && int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>Plain text for a description tag: tags and extra spaces removed, cut at a word with an ellipsis when too long.</summary>
    public static string Describe(string? text, int max = SeoLimits.MaxMetaDescription)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // A block tag is a break between words; an inline tag (bold, link) is not.
        var plain = WhitespacePattern().Replace(System.Net.WebUtility.HtmlDecode(TagPattern().Replace(BlockTagPattern().Replace(text, " "), string.Empty)), " ").Trim();
        if (plain.Length <= max) return plain;

        var cut = plain[..(max - 1)];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > max / 3 ? cut[..lastSpace] : cut).TrimEnd() + "…";
    }

    public static string Robots(string siteBaseUrl) =>
        string.Join("\n",
        [
            "User-agent: *",
            "Disallow: /admin",
            "Disallow: /vendor",
            "Disallow: /customer",
            "Disallow: /auth",
            "Disallow: /storefront/cart",
            "Disallow: /storefront/checkout",
            "Disallow: /payments",
            "",
            $"Sitemap: {siteBaseUrl.TrimEnd('/')}/sitemap.xml",
            ""
        ]);

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"</?(?:p|br|div|li|ul|ol|h[1-6]|tr|td|th|table)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagPattern();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}

public interface ISeoStore
{
    /// <summary>Visible products (live, shop active, publication window open), by id after <paramref name="afterId"/>.</summary>
    Task<IReadOnlyList<SitemapEntry>> ListProductsAsync(int afterId, int take, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Active shops that are not deleted, by id after <paramref name="afterId"/>.</summary>
    Task<IReadOnlyList<SitemapEntry>> ListShopsAsync(int afterId, int take, CancellationToken cancellationToken);
}

/// <summary>The names of what is public, from the catalog and the shops. Null when it does not exist or a customer cannot see it.</summary>
public interface ISeoSource
{
    Task<string?> GetPublicProductNameAsync(int id, CancellationToken cancellationToken);
    Task<string?> GetPublicShopNameAsync(int id, CancellationToken cancellationToken);
}

public interface ISeoService
{
    /// <summary>The canonical address for a key of a product or a shop, or "not found" when it is not public.</summary>
    Task<SeoPath> ResolvePathAsync(string? type, string? key, CancellationToken cancellationToken);

    Task<string> BuildSitemapAsync(CancellationToken cancellationToken);

    string BuildRobots();
}
