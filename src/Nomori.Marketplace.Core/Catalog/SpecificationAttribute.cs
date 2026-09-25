namespace Nomori.Marketplace.Core.Catalog;

public sealed class SpecificationAttributeGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public sealed class SpecificationAttributeDef
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? SpecificationAttributeGroupId { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class SpecificationAttributeOption
{
    public int Id { get; set; }
    public int SpecificationAttributeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ColorSquaresRgb { get; set; }
    public int DisplayOrder { get; set; }
}

public enum SpecificationAttributeType
{
    Option = 0,
    CustomText = 10,
    Hyperlink = 30
}

public sealed class ProductSpecificationMapping
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public SpecificationAttributeType AttributeType { get; set; }
    public int? SpecificationAttributeOptionId { get; set; }
    public string? CustomValue { get; set; }
    public bool AllowFiltering { get; set; }
    public bool ShowOnProductPage { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class ProductTag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

// Detail models
public sealed class ProductSpecDetail
{
    public IReadOnlyList<SpecGroupDetail> Groups { get; set; } = [];
    public IReadOnlyList<ProductSpecRow> Ungrouped { get; set; } = [];
}

public sealed class SpecGroupDetail
{
    public SpecificationAttributeGroup Group { get; set; } = null!;
    public IReadOnlyList<ProductSpecRow> Rows { get; set; } = [];
}

public sealed class ProductSpecRow
{
    public ProductSpecificationMapping Mapping { get; set; } = null!;
    public SpecificationAttributeDef SpecAttribute { get; set; } = null!;
    public string DisplayValue { get; set; } = string.Empty;
    public string? ColorSquaresRgb { get; set; }
}

// Commands
public sealed record CreateSpecGroupCommand(string Name, int DisplayOrder);
public sealed record UpdateSpecGroupCommand(int Id, string Name, int DisplayOrder);

public sealed record CreateSpecAttributeCommand(string Name, int? GroupId, int DisplayOrder);
public sealed record UpdateSpecAttributeCommand(int Id, string Name, int? GroupId, int DisplayOrder);

public sealed record CreateSpecOptionCommand(int SpecAttributeId, string Name, string? ColorSquaresRgb, int DisplayOrder);
public sealed record UpdateSpecOptionCommand(int Id, string Name, string? ColorSquaresRgb, int DisplayOrder);

public sealed record AddProductSpecCommand(
    int ProductId,
    SpecificationAttributeType AttributeType,
    int? SpecificationAttributeOptionId,
    string? CustomValue,
    bool AllowFiltering,
    bool ShowOnProductPage,
    int DisplayOrder);

public sealed record UpdateProductSpecCommand(
    int Id,
    SpecificationAttributeType AttributeType,
    int? SpecificationAttributeOptionId,
    string? CustomValue,
    bool AllowFiltering,
    bool ShowOnProductPage,
    int DisplayOrder);

// Store interface
public interface ISpecificationAttributeStore
{
    // Groups
    Task<IReadOnlyList<SpecificationAttributeGroup>> GetGroupsAsync(CancellationToken ct);
    Task<SpecificationAttributeGroup?> GetGroupAsync(int id, CancellationToken ct);
    Task<int> InsertGroupAsync(SpecificationAttributeGroup g, CancellationToken ct);
    Task UpdateGroupAsync(SpecificationAttributeGroup g, CancellationToken ct);
    Task DeleteGroupAsync(int id, CancellationToken ct);

    // Spec attributes
    Task<IReadOnlyList<SpecificationAttributeDef>> GetAllAsync(CancellationToken ct);
    Task<SpecificationAttributeDef?> GetAsync(int id, CancellationToken ct);
    Task<int> InsertAsync(SpecificationAttributeDef attr, CancellationToken ct);
    Task UpdateAsync(SpecificationAttributeDef attr, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);

    // Options
    Task<IReadOnlyList<SpecificationAttributeOption>> GetOptionsAsync(int specAttributeId, CancellationToken ct);
    Task<IReadOnlyList<SpecificationAttributeOption>> GetOptionsByIdsAsync(IEnumerable<int> ids, CancellationToken ct);
    Task<SpecificationAttributeOption?> GetOptionAsync(int id, CancellationToken ct);
    Task<int> InsertOptionAsync(SpecificationAttributeOption opt, CancellationToken ct);
    Task UpdateOptionAsync(SpecificationAttributeOption opt, CancellationToken ct);
    Task DeleteOptionAsync(int id, CancellationToken ct);

    // Product spec mappings
    Task<IReadOnlyList<ProductSpecificationMapping>> GetProductSpecsAsync(int productId, CancellationToken ct);
    Task<ProductSpecificationMapping?> GetProductSpecAsync(int id, CancellationToken ct);
    Task<int> InsertProductSpecAsync(ProductSpecificationMapping spec, CancellationToken ct);
    Task UpdateProductSpecAsync(ProductSpecificationMapping spec, CancellationToken ct);
    Task DeleteProductSpecAsync(int id, CancellationToken ct);

    // Tags
    Task<IReadOnlyList<ProductTag>> GetAllTagsAsync(CancellationToken ct);
    Task<IReadOnlyList<ProductTag>> GetTagsByProductAsync(int productId, CancellationToken ct);
    Task<ProductTag?> GetTagAsync(int id, CancellationToken ct);
    Task<ProductTag?> GetTagByNameAsync(string name, CancellationToken ct);
    Task<int> InsertTagAsync(ProductTag tag, CancellationToken ct);
    Task DeleteTagAsync(int id, CancellationToken ct);
    Task SetProductTagsAsync(int productId, int[] tagIds, CancellationToken ct);
}

// Service interface
public interface ISpecificationAttributeService
{
    Task<IReadOnlyList<SpecificationAttributeGroup>> GetGroupsAsync(CancellationToken ct);
    Task<CatalogResult<SpecificationAttributeGroup>> CreateGroupAsync(CreateSpecGroupCommand cmd, CancellationToken ct);
    Task<CatalogResult<SpecificationAttributeGroup>> UpdateGroupAsync(UpdateSpecGroupCommand cmd, CancellationToken ct);
    Task<bool> DeleteGroupAsync(int id, CancellationToken ct);

    Task<IReadOnlyList<SpecificationAttributeDef>> GetAllAsync(CancellationToken ct);
    Task<CatalogResult<SpecificationAttributeDef>> CreateAsync(CreateSpecAttributeCommand cmd, CancellationToken ct);
    Task<CatalogResult<SpecificationAttributeDef>> UpdateAsync(UpdateSpecAttributeCommand cmd, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);

    Task<IReadOnlyList<SpecificationAttributeOption>> GetOptionsAsync(int specAttributeId, CancellationToken ct);
    Task<CatalogResult<SpecificationAttributeOption>> CreateOptionAsync(CreateSpecOptionCommand cmd, CancellationToken ct);
    Task<CatalogResult<SpecificationAttributeOption>> UpdateOptionAsync(UpdateSpecOptionCommand cmd, CancellationToken ct);
    Task<bool> DeleteOptionAsync(int id, CancellationToken ct);

    Task<ProductSpecDetail> GetProductSpecDetailAsync(int productId, CancellationToken ct);
    Task<CatalogResult<ProductSpecificationMapping>> AddProductSpecAsync(AddProductSpecCommand cmd, CancellationToken ct);
    Task<CatalogResult<ProductSpecificationMapping>> UpdateProductSpecAsync(UpdateProductSpecCommand cmd, CancellationToken ct);
    Task<bool> DeleteProductSpecAsync(int id, CancellationToken ct);

    Task<IReadOnlyList<ProductTag>> GetAllTagsAsync(CancellationToken ct);
    Task<IReadOnlyList<ProductTag>> GetProductTagsAsync(int productId, CancellationToken ct);
    Task<CatalogResult<ProductTag>> EnsureTagAsync(string name, CancellationToken ct);
    Task SetProductTagsAsync(int productId, string[] tagNames, CancellationToken ct);
    Task<bool> DeleteTagAsync(int id, CancellationToken ct);
}
