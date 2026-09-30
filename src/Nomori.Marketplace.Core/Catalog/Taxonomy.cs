namespace Nomori.Marketplace.Core.Catalog;

/// <summary>Business-rule codes returned in <c>ProblemDetails.detail</c>. NotFound maps to 404; every other code to 409.</summary>
public static class CatalogErrors
{
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string ProductHiddenByAdmin = "product.hidden_by_admin";
    public const string ProductNotHidden = "product.not_hidden";
    public const string ProductAlreadyHidden = "product.already_hidden";
    public const string ProductInvalidTransition = "product.invalid_transition";
    public const string CategoryHasChildren = "category.has_children";
    public const string CategoryInUse = "category.in_use";
    public const string ManufacturerInUse = "manufacturer.in_use";
}

/// <summary>Who is attaching a product to the taxonomy. Sellers get stricter rules than platform administrators.</summary>
public enum TaxonomyAudience
{
    Admin = 0,
    Seller = 1
}

public static class TaxonomyLimits
{
    public const int MaxCategoriesPerProduct = 10;
    public const int MaxManufacturersPerProduct = 10;
}

/// <summary>A validated selection: duplicates removed, order preserved.</summary>
public sealed record TaxonomySelection(int[] CategoryIds, int[] ManufacturerIds);

public interface ITaxonomyService
{
    /// <summary>
    /// Checks that every category and manufacturer exists and, for sellers, is allowed. Errors are keyed
    /// <c>categoryIds</c> and <c>manufacturerIds</c>.
    /// </summary>
    Task<CatalogResult<TaxonomySelection>> ValidateSelectionAsync(
        int[] categoryIds, int[] manufacturerIds, TaxonomyAudience audience, CancellationToken cancellationToken);
}
