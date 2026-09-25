using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class SpecificationAttributeService(ISpecificationAttributeStore store) : ISpecificationAttributeService
{
    // ---- Groups ----

    public Task<IReadOnlyList<SpecificationAttributeGroup>> GetGroupsAsync(CancellationToken ct) => store.GetGroupsAsync(ct);

    public async Task<CatalogResult<SpecificationAttributeGroup>> CreateGroupAsync(CreateSpecGroupCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name)) return CatalogResult.Failure<SpecificationAttributeGroup>("name", "Name is required.");
        var g = new SpecificationAttributeGroup { Name = cmd.Name.Trim(), DisplayOrder = cmd.DisplayOrder };
        g.Id = await store.InsertGroupAsync(g, ct);
        return CatalogResult.Success(g);
    }

    public async Task<CatalogResult<SpecificationAttributeGroup>> UpdateGroupAsync(UpdateSpecGroupCommand cmd, CancellationToken ct)
    {
        var g = await store.GetGroupAsync(cmd.Id, ct);
        if (g is null) return CatalogResult.Failure<SpecificationAttributeGroup>("id", "Group not found.");
        if (string.IsNullOrWhiteSpace(cmd.Name)) return CatalogResult.Failure<SpecificationAttributeGroup>("name", "Name is required.");
        g.Name = cmd.Name.Trim();
        g.DisplayOrder = cmd.DisplayOrder;
        await store.UpdateGroupAsync(g, ct);
        return CatalogResult.Success(g);
    }

    public async Task<bool> DeleteGroupAsync(int id, CancellationToken ct)
    {
        if (await store.GetGroupAsync(id, ct) is null) return false;
        await store.DeleteGroupAsync(id, ct);
        return true;
    }

    // ---- Spec attributes ----

    public Task<IReadOnlyList<SpecificationAttributeDef>> GetAllAsync(CancellationToken ct) => store.GetAllAsync(ct);

    public async Task<CatalogResult<SpecificationAttributeDef>> CreateAsync(CreateSpecAttributeCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name)) return CatalogResult.Failure<SpecificationAttributeDef>("name", "Name is required.");
        var attr = new SpecificationAttributeDef { Name = cmd.Name.Trim(), SpecificationAttributeGroupId = cmd.GroupId, DisplayOrder = cmd.DisplayOrder };
        attr.Id = await store.InsertAsync(attr, ct);
        return CatalogResult.Success(attr);
    }

    public async Task<CatalogResult<SpecificationAttributeDef>> UpdateAsync(UpdateSpecAttributeCommand cmd, CancellationToken ct)
    {
        var attr = await store.GetAsync(cmd.Id, ct);
        if (attr is null) return CatalogResult.Failure<SpecificationAttributeDef>("id", "Specification attribute not found.");
        if (string.IsNullOrWhiteSpace(cmd.Name)) return CatalogResult.Failure<SpecificationAttributeDef>("name", "Name is required.");
        attr.Name = cmd.Name.Trim();
        attr.SpecificationAttributeGroupId = cmd.GroupId;
        attr.DisplayOrder = cmd.DisplayOrder;
        await store.UpdateAsync(attr, ct);
        return CatalogResult.Success(attr);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        if (await store.GetAsync(id, ct) is null) return false;
        await store.DeleteAsync(id, ct);
        return true;
    }

    // ---- Options ----

    public Task<IReadOnlyList<SpecificationAttributeOption>> GetOptionsAsync(int specAttributeId, CancellationToken ct) =>
        store.GetOptionsAsync(specAttributeId, ct);

    public async Task<CatalogResult<SpecificationAttributeOption>> CreateOptionAsync(CreateSpecOptionCommand cmd, CancellationToken ct)
    {
        if (await store.GetAsync(cmd.SpecAttributeId, ct) is null)
            return CatalogResult.Failure<SpecificationAttributeOption>("specAttributeId", "Specification attribute not found.");
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return CatalogResult.Failure<SpecificationAttributeOption>("name", "Name is required.");
        var opt = new SpecificationAttributeOption
        {
            SpecificationAttributeId = cmd.SpecAttributeId,
            Name = cmd.Name.Trim(),
            ColorSquaresRgb = cmd.ColorSquaresRgb,
            DisplayOrder = cmd.DisplayOrder
        };
        opt.Id = await store.InsertOptionAsync(opt, ct);
        return CatalogResult.Success(opt);
    }

    public async Task<CatalogResult<SpecificationAttributeOption>> UpdateOptionAsync(UpdateSpecOptionCommand cmd, CancellationToken ct)
    {
        var opt = await store.GetOptionAsync(cmd.Id, ct);
        if (opt is null) return CatalogResult.Failure<SpecificationAttributeOption>("id", "Option not found.");
        if (string.IsNullOrWhiteSpace(cmd.Name)) return CatalogResult.Failure<SpecificationAttributeOption>("name", "Name is required.");
        opt.Name = cmd.Name.Trim();
        opt.ColorSquaresRgb = cmd.ColorSquaresRgb;
        opt.DisplayOrder = cmd.DisplayOrder;
        await store.UpdateOptionAsync(opt, ct);
        return CatalogResult.Success(opt);
    }

    public async Task<bool> DeleteOptionAsync(int id, CancellationToken ct)
    {
        if (await store.GetOptionAsync(id, ct) is null) return false;
        await store.DeleteOptionAsync(id, ct);
        return true;
    }

    // ---- Product spec detail ----

    public async Task<ProductSpecDetail> GetProductSpecDetailAsync(int productId, CancellationToken ct)
    {
        var mappings = await store.GetProductSpecsAsync(productId, ct);
        var visible = mappings.Where(m => m.ShowOnProductPage).ToList();

        var optionIds = visible
            .Where(m => m.AttributeType == SpecificationAttributeType.Option && m.SpecificationAttributeOptionId.HasValue)
            .Select(m => m.SpecificationAttributeOptionId!.Value)
            .Distinct();

        var options = (await store.GetOptionsByIdsAsync(optionIds, ct)).ToDictionary(o => o.Id);
        var allAttrs = (await store.GetAllAsync(ct)).ToDictionary(a => a.Id);
        var groups = (await store.GetGroupsAsync(ct)).ToDictionary(g => g.Id);

        ProductSpecRow ToRow(ProductSpecificationMapping m)
        {
            SpecificationAttributeDef attr = new() { Name = "Unknown" };
            string displayValue = m.CustomValue ?? string.Empty;
            string? rgb = null;

            if (m.AttributeType == SpecificationAttributeType.Option && m.SpecificationAttributeOptionId.HasValue)
            {
                if (options.TryGetValue(m.SpecificationAttributeOptionId.Value, out var opt))
                {
                    displayValue = opt.Name;
                    rgb = opt.ColorSquaresRgb;
                    if (allAttrs.TryGetValue(opt.SpecificationAttributeId, out var a)) attr = a;
                }
            }
            else if (allAttrs.Values.FirstOrDefault() is { } fallback)
            {
                attr = fallback;
            }

            return new ProductSpecRow { Mapping = m, SpecAttribute = attr, DisplayValue = displayValue, ColorSquaresRgb = rgb };
        }

        var rows = visible.Select(ToRow).ToList();
        var groupedRows = rows.Where(r => r.SpecAttribute.SpecificationAttributeGroupId.HasValue)
            .GroupBy(r => r.SpecAttribute.SpecificationAttributeGroupId!.Value)
            .Select(g => new SpecGroupDetail
            {
                Group = groups.TryGetValue(g.Key, out var grp) ? grp : new SpecificationAttributeGroup { Id = 0, Name = "Other" },
                Rows = g.ToList()
            })
            .OrderBy(g => g.Group.DisplayOrder)
            .ToList();

        var ungrouped = rows.Where(r => !r.SpecAttribute.SpecificationAttributeGroupId.HasValue).ToList();

        return new ProductSpecDetail { Groups = groupedRows, Ungrouped = ungrouped };
    }

    public async Task<CatalogResult<ProductSpecificationMapping>> AddProductSpecAsync(AddProductSpecCommand cmd, CancellationToken ct)
    {
        if (cmd.AttributeType == SpecificationAttributeType.Option && cmd.SpecificationAttributeOptionId is null)
            return CatalogResult.Failure<ProductSpecificationMapping>("specificationAttributeOptionId", "Option ID is required for Option type.");

        var spec = new ProductSpecificationMapping
        {
            ProductId = cmd.ProductId,
            AttributeType = cmd.AttributeType,
            SpecificationAttributeOptionId = cmd.SpecificationAttributeOptionId,
            CustomValue = cmd.CustomValue,
            AllowFiltering = cmd.AllowFiltering,
            ShowOnProductPage = cmd.ShowOnProductPage,
            DisplayOrder = cmd.DisplayOrder
        };
        spec.Id = await store.InsertProductSpecAsync(spec, ct);
        return CatalogResult.Success(spec);
    }

    public async Task<CatalogResult<ProductSpecificationMapping>> UpdateProductSpecAsync(UpdateProductSpecCommand cmd, CancellationToken ct)
    {
        var spec = await store.GetProductSpecAsync(cmd.Id, ct);
        if (spec is null) return CatalogResult.Failure<ProductSpecificationMapping>("id", "Spec not found.");
        spec.AttributeType = cmd.AttributeType;
        spec.SpecificationAttributeOptionId = cmd.SpecificationAttributeOptionId;
        spec.CustomValue = cmd.CustomValue;
        spec.AllowFiltering = cmd.AllowFiltering;
        spec.ShowOnProductPage = cmd.ShowOnProductPage;
        spec.DisplayOrder = cmd.DisplayOrder;
        await store.UpdateProductSpecAsync(spec, ct);
        return CatalogResult.Success(spec);
    }

    public async Task<bool> DeleteProductSpecAsync(int id, CancellationToken ct)
    {
        if (await store.GetProductSpecAsync(id, ct) is null) return false;
        await store.DeleteProductSpecAsync(id, ct);
        return true;
    }

    // ---- Tags ----

    public Task<IReadOnlyList<ProductTag>> GetAllTagsAsync(CancellationToken ct) => store.GetAllTagsAsync(ct);

    public Task<IReadOnlyList<ProductTag>> GetProductTagsAsync(int productId, CancellationToken ct) =>
        store.GetTagsByProductAsync(productId, ct);

    public async Task<CatalogResult<ProductTag>> EnsureTagAsync(string name, CancellationToken ct)
    {
        name = name.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return CatalogResult.Failure<ProductTag>("name", "Tag name is required.");
        var existing = await store.GetTagByNameAsync(name, ct);
        if (existing is not null) return CatalogResult.Success(existing);
        var tag = new ProductTag { Name = name };
        tag.Id = await store.InsertTagAsync(tag, ct);
        return CatalogResult.Success(tag);
    }

    public async Task SetProductTagsAsync(int productId, string[] tagNames, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var name in tagNames.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct())
        {
            var result = await EnsureTagAsync(name, ct);
            if (result.Succeeded) ids.Add(result.Value!.Id);
        }
        await store.SetProductTagsAsync(productId, [.. ids], ct);
    }

    public async Task<bool> DeleteTagAsync(int id, CancellationToken ct)
    {
        if (await store.GetTagAsync(id, ct) is null) return false;
        await store.DeleteTagAsync(id, ct);
        return true;
    }
}
