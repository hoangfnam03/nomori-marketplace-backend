using AngleSharp.Html.Dom;
using Ganss.Xss;

namespace Nomori.Marketplace.Services.Catalog;

/// <summary>Cleans product descriptions with an allow list: basic formatting and safe links only.</summary>
public static class HtmlContent
{
    public const int MaxLength = 20_000;

    private static readonly string[] Tags =
        ["p", "br", "strong", "b", "em", "i", "u", "s", "ul", "ol", "li", "h2", "h3", "h4", "blockquote", "a"];

    /// <summary>Returns the cleaned markup, or null when nothing is left. Scripts, styles, event handlers, images, frames and unsafe links are removed.</summary>
    public static string? Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        // A sanitizer is cheap to build and not meant to be reconfigured while shared, so each call gets its own.
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in Tags) sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowDataAttributes = false;
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IHtmlAnchorElement anchor) anchor.SetAttribute("rel", "noopener nofollow");
        };

        var clean = sanitizer.Sanitize(html).Trim();
        return clean.Length == 0 ? null : clean;
    }
}
