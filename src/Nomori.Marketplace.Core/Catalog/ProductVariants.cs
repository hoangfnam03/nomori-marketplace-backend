namespace Nomori.Marketplace.Core.Catalog;

/// <summary>One value of a variant attribute, for example "Red".</summary>
public sealed record VariantValueInput(string Name, string? ColorSquaresRgb = null, decimal PriceAdjustment = 0);

/// <summary>A product attribute (chosen from the platform list) with the values this product offers.</summary>
public sealed record VariantAttributeInput(int ProductAttributeId, bool IsRequired, IReadOnlyList<VariantValueInput> Values);

/// <summary>
/// One sellable variant. <see cref="ValueIndexes"/> has one index per attribute, in attribute order,
/// pointing into that attribute's values.
/// </summary>
public sealed record VariantCombinationInput(int[] ValueIndexes, string? Sku, int StockQuantity, decimal? OverriddenPrice);

public sealed record SaveVariantsCommand(
    IReadOnlyList<VariantAttributeInput> Attributes,
    IReadOnlyList<VariantCombinationInput> Combinations);

public sealed record SpecAttributeWithOptions(
    SpecificationAttributeDef Attribute, string? GroupName, IReadOnlyList<SpecificationAttributeOption> Options);

/// <summary>Platform-owned definitions a seller may choose from.</summary>
public sealed record OptionCatalog(
    IReadOnlyList<ProductAttributeSpec> Attributes, IReadOnlyList<SpecAttributeWithOptions> SpecAttributes);

public static class VariantLimits
{
    public const int MaxAttributes = 3;
    public const int MaxValuesPerAttribute = 20;
    public const int MaxCombinations = 100;
    public const int MaxStock = 1_000_000;
    public const int MaxSpecOptions = 30;
    public const int MaxTags = 20;
    public const int MaxTextLength = 100;
}

/// <summary>Variants, specifications and tags of the products of one shop. Products of other shops do not exist for the caller.</summary>
public interface IVendorProductDetailsService
{
    Task<OptionCatalog> GetOptionCatalogAsync(CancellationToken cancellationToken);

    Task<CatalogResult<string[]>> GetTagsAsync(int vendorId, int productId, CancellationToken cancellationToken);
    Task<CatalogResult<string[]>> SetTagsAsync(int vendorId, int productId, string[]? tagNames, int actorCustomerId, CancellationToken cancellationToken);

    Task<CatalogResult<int[]>> GetSpecOptionIdsAsync(int vendorId, int productId, CancellationToken cancellationToken);
    Task<CatalogResult<int[]>> SetSpecOptionsAsync(int vendorId, int productId, int[]? optionIds, int actorCustomerId, CancellationToken cancellationToken);

    Task<CatalogResult<ProductAttributeDetail>> GetVariantsAsync(int vendorId, int productId, CancellationToken cancellationToken);

    /// <summary>Replaces attributes, values and combinations; the product stock becomes the sum of the combination stocks.</summary>
    Task<CatalogResult<ProductAttributeDetail>> SetVariantsAsync(int vendorId, int productId, SaveVariantsCommand command, int actorCustomerId, CancellationToken cancellationToken);
}
