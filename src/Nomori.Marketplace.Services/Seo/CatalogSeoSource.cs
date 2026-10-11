using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Seo;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Seo;

/// <summary>Reads the catalog and the shops with the same visibility rules as the public pages.</summary>
public sealed class CatalogSeoSource(IProductService products, IVendorService vendors, IClock clock) : ISeoSource
{
    public async Task<string?> GetPublicProductNameAsync(int id, CancellationToken cancellationToken)
    {
        // Live, the shop active, the publication window open: what a customer can open.
        var product = await products.GetPublicProductAsync(id, cancellationToken);
        return product is not null && product.IsVisibleAt(clock.UtcNow) ? product.Name : null;
    }

    public async Task<string?> GetPublicShopNameAsync(int id, CancellationToken cancellationToken)
    {
        var vendor = await vendors.GetAsync(id, cancellationToken);
        return vendor is { Active: true, Deleted: false } ? vendor.Name : null;
    }
}
