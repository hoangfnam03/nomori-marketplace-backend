using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class ProductAttributeService(IProductAttributeStore store) : IProductAttributeService
{
    public Task<IReadOnlyList<ProductAttributeSpec>> GetAllAttributesAsync(CancellationToken ct) =>
        store.GetAllAttributesAsync(ct);

    public async Task<CatalogResult<ProductAttributeSpec>> CreateAttributeAsync(CreateProductAttributeCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return CatalogResult.Failure<ProductAttributeSpec>("name", "Name is required.");

        var attr = new ProductAttributeSpec { Name = cmd.Name.Trim(), Description = cmd.Description, DisplayOrder = cmd.DisplayOrder };
        attr.Id = await store.InsertAttributeAsync(attr, ct);
        return CatalogResult.Success(attr);
    }

    public async Task<CatalogResult<ProductAttributeSpec>> UpdateAttributeAsync(UpdateProductAttributeCommand cmd, CancellationToken ct)
    {
        var attr = await store.GetAttributeAsync(cmd.Id, ct);
        if (attr is null) return CatalogResult.Failure<ProductAttributeSpec>("id", "Attribute not found.");
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return CatalogResult.Failure<ProductAttributeSpec>("name", "Name is required.");

        attr.Name = cmd.Name.Trim();
        attr.Description = cmd.Description;
        attr.DisplayOrder = cmd.DisplayOrder;
        await store.UpdateAttributeAsync(attr, ct);
        return CatalogResult.Success(attr);
    }

    public async Task<bool> DeleteAttributeAsync(int id, CancellationToken ct)
    {
        var attr = await store.GetAttributeAsync(id, ct);
        if (attr is null) return false;
        await store.DeleteAttributeAsync(id, ct);
        return true;
    }

    public async Task<ProductAttributeDetail> GetProductAttributeDetailAsync(int productId, CancellationToken ct)
    {
        var mappings = await store.GetMappingsAsync(productId, ct);
        var allValues = await store.GetValuesByProductAsync(productId, ct);
        var combinations = await store.GetCombinationsAsync(productId, ct);
        var allAttributes = await store.GetAllAttributesAsync(ct);

        var attrById = allAttributes.ToDictionary(a => a.Id);
        var valuesByMappingId = allValues.GroupBy(v => v.ProductAttributeMappingId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProductAttributeValue>)g.ToList());

        var details = mappings.Select(m => new ProductAttributeMappingDetail
        {
            Mapping = m,
            Attribute = attrById.TryGetValue(m.ProductAttributeId, out var a) ? a : new ProductAttributeSpec { Name = "Unknown" },
            Values = valuesByMappingId.TryGetValue(m.Id, out var vals) ? vals : []
        }).ToList();

        return new ProductAttributeDetail { Mappings = details, Combinations = combinations };
    }

    public async Task<CatalogResult<ProductAttributeMapping>> AddMappingAsync(CreateAttributeMappingCommand cmd, CancellationToken ct)
    {
        var attr = await store.GetAttributeAsync(cmd.ProductAttributeId, ct);
        if (attr is null) return CatalogResult.Failure<ProductAttributeMapping>("productAttributeId", "Attribute not found.");

        var mapping = new ProductAttributeMapping
        {
            ProductId = cmd.ProductId,
            ProductAttributeId = cmd.ProductAttributeId,
            TextPrompt = cmd.TextPrompt,
            IsRequired = cmd.IsRequired,
            ControlType = cmd.ControlType,
            DisplayOrder = cmd.DisplayOrder
        };
        mapping.Id = await store.InsertMappingAsync(mapping, ct);
        return CatalogResult.Success(mapping);
    }

    public async Task<CatalogResult<ProductAttributeMapping>> UpdateMappingAsync(UpdateAttributeMappingCommand cmd, CancellationToken ct)
    {
        var mapping = await store.GetMappingAsync(cmd.Id, ct);
        if (mapping is null) return CatalogResult.Failure<ProductAttributeMapping>("id", "Mapping not found.");

        mapping.TextPrompt = cmd.TextPrompt;
        mapping.IsRequired = cmd.IsRequired;
        mapping.ControlType = cmd.ControlType;
        mapping.DisplayOrder = cmd.DisplayOrder;
        await store.UpdateMappingAsync(mapping, ct);
        return CatalogResult.Success(mapping);
    }

    public async Task<bool> DeleteMappingAsync(int id, CancellationToken ct)
    {
        var mapping = await store.GetMappingAsync(id, ct);
        if (mapping is null) return false;
        await store.DeleteMappingAsync(id, ct);
        return true;
    }

    public async Task<CatalogResult<ProductAttributeValue>> AddValueAsync(CreateAttributeValueCommand cmd, CancellationToken ct)
    {
        var mapping = await store.GetMappingAsync(cmd.ProductAttributeMappingId, ct);
        if (mapping is null) return CatalogResult.Failure<ProductAttributeValue>("productAttributeMappingId", "Mapping not found.");
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return CatalogResult.Failure<ProductAttributeValue>("name", "Name is required.");

        var value = new ProductAttributeValue
        {
            ProductAttributeMappingId = cmd.ProductAttributeMappingId,
            Name = cmd.Name.Trim(),
            ColorSquaresRgb = cmd.ColorSquaresRgb,
            PriceAdjustment = cmd.PriceAdjustment,
            IsPreSelected = cmd.IsPreSelected,
            DisplayOrder = cmd.DisplayOrder
        };
        value.Id = await store.InsertValueAsync(value, ct);
        return CatalogResult.Success(value);
    }

    public async Task<CatalogResult<ProductAttributeValue>> UpdateValueAsync(UpdateAttributeValueCommand cmd, CancellationToken ct)
    {
        var value = await store.GetValueAsync(cmd.Id, ct);
        if (value is null) return CatalogResult.Failure<ProductAttributeValue>("id", "Value not found.");
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return CatalogResult.Failure<ProductAttributeValue>("name", "Name is required.");

        value.Name = cmd.Name.Trim();
        value.ColorSquaresRgb = cmd.ColorSquaresRgb;
        value.PriceAdjustment = cmd.PriceAdjustment;
        value.IsPreSelected = cmd.IsPreSelected;
        value.DisplayOrder = cmd.DisplayOrder;
        await store.UpdateValueAsync(value, ct);
        return CatalogResult.Success(value);
    }

    public async Task<bool> DeleteValueAsync(int id, CancellationToken ct)
    {
        var value = await store.GetValueAsync(id, ct);
        if (value is null) return false;
        await store.DeleteValueAsync(id, ct);
        return true;
    }

    public async Task<CatalogResult<ProductAttributeCombination>> AddCombinationAsync(CreateCombinationCommand cmd, CancellationToken ct)
    {
        var combo = new ProductAttributeCombination
        {
            ProductId = cmd.ProductId,
            AttributesJson = cmd.AttributesJson,
            StockQuantity = cmd.StockQuantity,
            AllowOutOfStockOrders = cmd.AllowOutOfStockOrders,
            Sku = cmd.Sku,
            OverriddenPrice = cmd.OverriddenPrice
        };
        combo.Id = await store.InsertCombinationAsync(combo, ct);
        return CatalogResult.Success(combo);
    }

    public async Task<CatalogResult<ProductAttributeCombination>> UpdateCombinationAsync(UpdateCombinationCommand cmd, CancellationToken ct)
    {
        var combo = await store.GetCombinationAsync(cmd.Id, ct);
        if (combo is null) return CatalogResult.Failure<ProductAttributeCombination>("id", "Combination not found.");

        combo.AttributesJson = cmd.AttributesJson;
        combo.StockQuantity = cmd.StockQuantity;
        combo.AllowOutOfStockOrders = cmd.AllowOutOfStockOrders;
        combo.Sku = cmd.Sku;
        combo.OverriddenPrice = cmd.OverriddenPrice;
        await store.UpdateCombinationAsync(combo, ct);
        return CatalogResult.Success(combo);
    }

    public async Task<bool> DeleteCombinationAsync(int id, CancellationToken ct)
    {
        var combo = await store.GetCombinationAsync(id, ct);
        if (combo is null) return false;
        await store.DeleteCombinationAsync(id, ct);
        return true;
    }
}
