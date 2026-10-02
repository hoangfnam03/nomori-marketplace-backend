using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Services.Tests;

/// <summary>In-memory attribute store: implements what the variants service uses and keeps what ReplaceVariantsAsync receives.</summary>
internal sealed class FakeAttributeStore : IProductAttributeStore
{
    private int nextId = 100;

    public List<ProductAttributeSpec> Attributes { get; } = [];
    public Dictionary<int, SaveVariantsCommand> Saved { get; } = [];

    /// <summary>SKUs that "belong" to other products of the shop.</summary>
    public HashSet<string> TakenSkus { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ProductAttributeMapping> mappings = [];
    private readonly List<ProductAttributeValue> values = [];
    private readonly List<ProductAttributeCombination> combinations = [];

    public List<ProductAttributeCombination> Combinations => combinations;
    public int? LastActor { get; private set; }

    public Task<IReadOnlyList<ProductAttributeSpec>> GetAllAttributesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProductAttributeSpec>>(Attributes);

    public Task<ProductAttributeSpec?> GetAttributeAsync(int id, CancellationToken ct) =>
        Task.FromResult(Attributes.FirstOrDefault(a => a.Id == id));

    public Task<IReadOnlyList<ProductAttributeMapping>> GetMappingsAsync(int productId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProductAttributeMapping>>(mappings.Where(m => m.ProductId == productId).ToList());

    public Task<IReadOnlyList<ProductAttributeValue>> GetValuesByProductAsync(int productId, CancellationToken ct)
    {
        var mappingIds = mappings.Where(m => m.ProductId == productId).Select(m => m.Id).ToHashSet();
        return Task.FromResult<IReadOnlyList<ProductAttributeValue>>(values.Where(v => mappingIds.Contains(v.ProductAttributeMappingId)).ToList());
    }

    public Task<IReadOnlyList<ProductAttributeCombination>> GetCombinationsAsync(int productId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProductAttributeCombination>>(combinations.Where(c => c.ProductId == productId).ToList());

    public Task<bool> IsCombinationSkuTakenAsync(int vendorId, string sku, int excludeProductId, CancellationToken ct) =>
        Task.FromResult(TakenSkus.Contains(sku));

    public Task ReplaceVariantsAsync(int productId, SaveVariantsCommand command, int? actorCustomerId, CancellationToken ct)
    {
        Saved[productId] = command;
        LastActor = actorCustomerId;
        foreach (var mapping in mappings.Where(m => m.ProductId == productId).ToList())
        {
            values.RemoveAll(v => v.ProductAttributeMappingId == mapping.Id);
            mappings.Remove(mapping);
        }
        combinations.RemoveAll(c => c.ProductId == productId);

        var valueIds = new List<int[]>();
        var mappingIds = new List<int>();
        for (var a = 0; a < command.Attributes.Count; a++)
        {
            var mapping = new ProductAttributeMapping { Id = nextId++, ProductId = productId, ProductAttributeId = command.Attributes[a].ProductAttributeId, DisplayOrder = a };
            mappings.Add(mapping);
            mappingIds.Add(mapping.Id);
            valueIds.Add(command.Attributes[a].Values.Select((v, i) =>
            {
                var value = new ProductAttributeValue { Id = nextId++, ProductAttributeMappingId = mapping.Id, Name = v.Name, DisplayOrder = i };
                values.Add(value);
                return value.Id;
            }).ToArray());
        }

        foreach (var c in command.Combinations)
        {
            var key = mappingIds.Select((m, a) => $"\"{m}\":{valueIds[a][c.ValueIndexes[a]]}");
            combinations.Add(new ProductAttributeCombination
            {
                Id = nextId++, ProductId = productId, AttributesJson = "{" + string.Join(',', key) + "}",
                StockQuantity = c.StockQuantity, Sku = c.Sku, OverriddenPrice = c.OverriddenPrice
            });
        }
        return Task.CompletedTask;
    }

    public Task<int> InsertAttributeAsync(ProductAttributeSpec attr, CancellationToken ct) => throw new NotSupportedException();
    public Task UpdateAttributeAsync(ProductAttributeSpec attr, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAttributeAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ProductAttributeMapping?> GetMappingAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> InsertMappingAsync(ProductAttributeMapping mapping, CancellationToken ct) => throw new NotSupportedException();
    public Task UpdateMappingAsync(ProductAttributeMapping mapping, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteMappingAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<ProductAttributeValue>> GetValuesAsync(int mappingId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ProductAttributeValue?> GetValueAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> InsertValueAsync(ProductAttributeValue value, CancellationToken ct) => throw new NotSupportedException();
    public Task UpdateValueAsync(ProductAttributeValue value, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteValueAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ProductAttributeCombination?> GetCombinationAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> InsertCombinationAsync(ProductAttributeCombination combo, CancellationToken ct) => throw new NotSupportedException();
    public Task UpdateCombinationAsync(ProductAttributeCombination combo, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteCombinationAsync(int id, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>In-memory specification and tag store for the parts the variants service uses.</summary>
internal sealed class FakeSpecificationStore : ISpecificationAttributeStore
{
    private int nextTagId = 1;

    public List<SpecificationAttributeOption> Options { get; } = [];
    public List<ProductTag> Tags { get; } = [];
    public List<ProductSpecificationMapping> ProductSpecs { get; } = [];
    public Dictionary<int, int[]> ProductTags { get; } = [];

    public Task<IReadOnlyList<SpecificationAttributeGroup>> GetGroupsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SpecificationAttributeGroup>>([]);

    public Task<IReadOnlyList<SpecificationAttributeDef>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SpecificationAttributeDef>>(
            Options.Select(o => o.SpecificationAttributeId).Distinct().Select(id => new SpecificationAttributeDef { Id = id, Name = "Spec " + id }).ToList());

    public Task<IReadOnlyList<SpecificationAttributeOption>> GetOptionsAsync(int specAttributeId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SpecificationAttributeOption>>(Options.Where(o => o.SpecificationAttributeId == specAttributeId).ToList());

    public Task<IReadOnlyList<SpecificationAttributeOption>> GetOptionsByIdsAsync(IEnumerable<int> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SpecificationAttributeOption>>(Options.Where(o => ids.Contains(o.Id)).ToList());

    public Task<SpecificationAttributeOption?> GetOptionAsync(int id, CancellationToken ct) =>
        Task.FromResult(Options.FirstOrDefault(o => o.Id == id));

    public Task<IReadOnlyList<ProductSpecificationMapping>> GetProductSpecsAsync(int productId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProductSpecificationMapping>>(ProductSpecs.Where(s => s.ProductId == productId).ToList());

    public Task ReplaceProductOptionSpecsAsync(int productId, int[] optionIds, CancellationToken ct)
    {
        ProductSpecs.RemoveAll(s => s.ProductId == productId && s.AttributeType == SpecificationAttributeType.Option);
        ProductSpecs.AddRange(optionIds.Select((id, i) => new ProductSpecificationMapping
        {
            ProductId = productId, AttributeType = SpecificationAttributeType.Option, SpecificationAttributeOptionId = id, DisplayOrder = i
        }));
        return Task.CompletedTask;
    }

    public Task<ProductTag?> GetTagByNameAsync(string name, CancellationToken ct) =>
        Task.FromResult(Tags.FirstOrDefault(t => t.Name == name));

    public Task<int> InsertTagAsync(ProductTag tag, CancellationToken ct)
    {
        tag.Id = nextTagId++;
        Tags.Add(tag);
        return Task.FromResult(tag.Id);
    }

    public Task SetProductTagsAsync(int productId, int[] tagIds, CancellationToken ct)
    {
        ProductTags[productId] = tagIds;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProductTag>> GetTagsByProductAsync(int productId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProductTag>>(
            (ProductTags.GetValueOrDefault(productId) ?? []).Select(id => Tags.Single(t => t.Id == id)).ToList());

    public Task<SpecificationAttributeGroup?> GetGroupAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> InsertGroupAsync(SpecificationAttributeGroup g, CancellationToken ct) => throw new NotSupportedException();
    public Task UpdateGroupAsync(SpecificationAttributeGroup g, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteGroupAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<SpecificationAttributeDef?> GetAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> InsertAsync(SpecificationAttributeDef attr, CancellationToken ct) => throw new NotSupportedException();
    public Task UpdateAsync(SpecificationAttributeDef attr, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> InsertOptionAsync(SpecificationAttributeOption opt, CancellationToken ct) => throw new NotSupportedException();
    public Task UpdateOptionAsync(SpecificationAttributeOption opt, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteOptionAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ProductSpecificationMapping?> GetProductSpecAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> InsertProductSpecAsync(ProductSpecificationMapping spec, CancellationToken ct) => throw new NotSupportedException();
    public Task UpdateProductSpecAsync(ProductSpecificationMapping spec, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteProductSpecAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<ProductTag>> GetAllTagsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<ProductTag?> GetTagAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteTagAsync(int id, CancellationToken ct) => throw new NotSupportedException();
}
