using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Seo;

namespace Nomori.Marketplace.Api.Modules.Seo;

public sealed record SeoPathResponse(bool Found, string CanonicalPath, bool IsCanonical)
{
    public static SeoPathResponse From(SeoPath path) => new(path.Found, path.CanonicalPath, path.IsCanonical);
}

/// <summary>
/// What crawlers read: the canonical address of a page, the sitemap and robots.txt. Everything is public and read-only, and only what a
/// customer can see is answered. The last two sit at the root because that is where crawlers look; the storefront server passes them on.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class SeoController(ISeoService seo) : ControllerBase
{
    [HttpGet("api/v1/seo/path")]
    public async Task<IActionResult> Path([FromQuery] string? type, [FromQuery] string? key, CancellationToken cancellationToken) =>
        Ok(SeoPathResponse.From(await seo.ResolvePathAsync(type, key, cancellationToken)));

    [HttpGet("sitemap.xml")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Sitemap(CancellationToken cancellationToken) =>
        Content(await seo.BuildSitemapAsync(cancellationToken), "application/xml", Encoding.UTF8);

    [HttpGet("robots.txt")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public IActionResult Robots() => Content(seo.BuildRobots(), "text/plain", Encoding.UTF8);
}
