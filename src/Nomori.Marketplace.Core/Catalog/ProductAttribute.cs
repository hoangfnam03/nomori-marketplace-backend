namespace Nomori.Marketplace.Core.Catalog;

public enum AttributeControlType
{
    DropdownList = 1,
    RadioList = 2,
    Checkboxes = 3,
    TextBox = 4,
    MultilineTextbox = 10,
    ColorSquares = 40,
    ImageSquares = 45,
    ReadonlyCheckboxes = 50
}

public sealed class ProductAttributeSpec
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class ProductAttributeMapping
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int ProductAttributeId { get; set; }
    public string? TextPrompt { get; set; }
    public bool IsRequired { get; set; }
    public AttributeControlType ControlType { get; set; } = AttributeControlType.DropdownList;
    public int DisplayOrder { get; set; }
}

public sealed class ProductAttributeValue
{
    public int Id { get; set; }
    public int ProductAttributeMappingId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ColorSquaresRgb { get; set; }
    public decimal PriceAdjustment { get; set; }
    public bool IsPreSelected { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class ProductAttributeCombination
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string AttributesJson { get; set; } = "{}";
    public int StockQuantity { get; set; }
    public bool AllowOutOfStockOrders { get; set; }
    public string? Sku { get; set; }
    public decimal? OverriddenPrice { get; set; }
}

public sealed class ProductAttributeMappingDetail
{
    public ProductAttributeMapping Mapping { get; set; } = null!;
    public ProductAttributeSpec Attribute { get; set; } = null!;
    public IReadOnlyList<ProductAttributeValue> Values { get; set; } = [];
}

public sealed class ProductAttributeDetail
{
    public IReadOnlyList<ProductAttributeMappingDetail> Mappings { get; set; } = [];
    public IReadOnlyList<ProductAttributeCombination> Combinations { get; set; } = [];
}

// Commands
public sealed record CreateProductAttributeCommand(string Name, string? Description, int DisplayOrder);
public sealed record UpdateProductAttributeCommand(int Id, string Name, string? Description, int DisplayOrder);

public sealed record CreateAttributeMappingCommand(
    int ProductId, int ProductAttributeId,
    string? TextPrompt, bool IsRequired,
    AttributeControlType ControlType, int DisplayOrder);

public sealed record UpdateAttributeMappingCommand(
    int Id, string? TextPrompt, bool IsRequired,
    AttributeControlType ControlType, int DisplayOrder);

public sealed record CreateAttributeValueCommand(
    int ProductAttributeMappingId, string Name,
    string? ColorSquaresRgb, decimal PriceAdjustment,
    bool IsPreSelected, int DisplayOrder);

public sealed record UpdateAttributeValueCommand(
    int Id, string Name, string? ColorSquaresRgb,
    decimal PriceAdjustment, bool IsPreSelected, int DisplayOrder);

public sealed record CreateCombinationCommand(
    int ProductId, string AttributesJson,
    int StockQuantity, bool AllowOutOfStockOrders,
    string? Sku, decimal? OverriddenPrice);

public sealed record UpdateCombinationCommand(
    int Id, string AttributesJson,
    int StockQuantity, bool AllowOutOfStockOrders,
    string? Sku, decimal? OverriddenPrice);

// Store interface
public interface IProductAttributeStore
{
    Task<IReadOnlyList<ProductAttributeSpec>> GetAllAttributesAsync(CancellationToken ct);
    Task<ProductAttributeSpec?> GetAttributeAsync(int id, CancellationToken ct);
    Task<int> InsertAttributeAsync(ProductAttributeSpec attr, CancellationToken ct);
    Task UpdateAttributeAsync(ProductAttributeSpec attr, CancellationToken ct);
    Task DeleteAttributeAsync(int id, CancellationToken ct);

    Task<IReadOnlyList<ProductAttributeMapping>> GetMappingsAsync(int productId, CancellationToken ct);
    Task<ProductAttributeMapping?> GetMappingAsync(int id, CancellationToken ct);
    Task<int> InsertMappingAsync(ProductAttributeMapping mapping, CancellationToken ct);
    Task UpdateMappingAsync(ProductAttributeMapping mapping, CancellationToken ct);
    Task DeleteMappingAsync(int id, CancellationToken ct);

    Task<IReadOnlyList<ProductAttributeValue>> GetValuesAsync(int mappingId, CancellationToken ct);
    Task<IReadOnlyList<ProductAttributeValue>> GetValuesByProductAsync(int productId, CancellationToken ct);
    Task<ProductAttributeValue?> GetValueAsync(int id, CancellationToken ct);
    Task<int> InsertValueAsync(ProductAttributeValue value, CancellationToken ct);
    Task UpdateValueAsync(ProductAttributeValue value, CancellationToken ct);
    Task DeleteValueAsync(int id, CancellationToken ct);

    Task<IReadOnlyList<ProductAttributeCombination>> GetCombinationsAsync(int productId, CancellationToken ct);
    Task<ProductAttributeCombination?> GetCombinationAsync(int id, CancellationToken ct);
    Task<int> InsertCombinationAsync(ProductAttributeCombination combo, CancellationToken ct);
    Task UpdateCombinationAsync(ProductAttributeCombination combo, CancellationToken ct);
    Task DeleteCombinationAsync(int id, CancellationToken ct);
}

// Service interface
public interface IProductAttributeService
{
    Task<IReadOnlyList<ProductAttributeSpec>> GetAllAttributesAsync(CancellationToken ct);
    Task<CatalogResult<ProductAttributeSpec>> CreateAttributeAsync(CreateProductAttributeCommand cmd, CancellationToken ct);
    Task<CatalogResult<ProductAttributeSpec>> UpdateAttributeAsync(UpdateProductAttributeCommand cmd, CancellationToken ct);
    Task<bool> DeleteAttributeAsync(int id, CancellationToken ct);

    Task<ProductAttributeDetail> GetProductAttributeDetailAsync(int productId, CancellationToken ct);

    Task<CatalogResult<ProductAttributeMapping>> AddMappingAsync(CreateAttributeMappingCommand cmd, CancellationToken ct);
    Task<CatalogResult<ProductAttributeMapping>> UpdateMappingAsync(UpdateAttributeMappingCommand cmd, CancellationToken ct);
    Task<bool> DeleteMappingAsync(int id, CancellationToken ct);

    Task<CatalogResult<ProductAttributeValue>> AddValueAsync(CreateAttributeValueCommand cmd, CancellationToken ct);
    Task<CatalogResult<ProductAttributeValue>> UpdateValueAsync(UpdateAttributeValueCommand cmd, CancellationToken ct);
    Task<bool> DeleteValueAsync(int id, CancellationToken ct);

    Task<CatalogResult<ProductAttributeCombination>> AddCombinationAsync(CreateCombinationCommand cmd, CancellationToken ct);
    Task<CatalogResult<ProductAttributeCombination>> UpdateCombinationAsync(UpdateCombinationCommand cmd, CancellationToken ct);
    Task<bool> DeleteCombinationAsync(int id, CancellationToken ct);
}
