using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Api.Modules.Catalog;

/// <summary>The camelCase names the API uses for <see cref="ProductStatus"/>: draft, live, stopped, hiddenByAdmin.</summary>
internal static class ProductStatusNames
{
    public static string ToName(ProductStatus status) =>
        char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..];

    public static bool TryParse(string? value, out ProductStatus status) =>
        Enum.TryParse(value, ignoreCase: true, out status) && Enum.IsDefined(status);
}
