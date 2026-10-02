using System.Text.RegularExpressions;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Catalog;

public sealed partial class VendorProductDetailsService(
    IProductStore productStore,
    IVendorStore vendorStore,
    IProductAttributeStore attributeStore,
    IProductAttributeService attributeService,
    ISpecificationAttributeStore specStore,
    IInventoryStore inventoryStore,
    IAuditLogService auditLog,
    IClock clock) : IVendorProductDetailsService
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    public async Task<OptionCatalog> GetOptionCatalogAsync(CancellationToken cancellationToken)
    {
        var attributes = await attributeStore.GetAllAttributesAsync(cancellationToken);
        var groups = (await specStore.GetGroupsAsync(cancellationToken)).ToDictionary(g => g.Id);

        var specs = new List<SpecAttributeWithOptions>();
        foreach (var attribute in await specStore.GetAllAsync(cancellationToken))
        {
            var options = await specStore.GetOptionsAsync(attribute.Id, cancellationToken);
            var groupName = attribute.SpecificationAttributeGroupId is { } gid && groups.TryGetValue(gid, out var group) ? group.Name : null;
            specs.Add(new SpecAttributeWithOptions(attribute, groupName, options));
        }
        return new OptionCatalog(attributes, specs);
    }

    // ---- Tags ----

    public async Task<CatalogResult<string[]>> GetTagsAsync(int vendorId, int productId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadAsync<string[]>(vendorId, productId, forWrite: false, cancellationToken);
        if (failure is not null) return failure;
        return CatalogResult.Success((await specStore.GetTagsByProductAsync(product!.Id, cancellationToken)).Select(t => t.Name).ToArray());
    }

    public async Task<CatalogResult<string[]>> SetTagsAsync(
        int vendorId, int productId, string[]? tagNames, int actorCustomerId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadAsync<string[]>(vendorId, productId, forWrite: true, cancellationToken);
        if (failure is not null) return failure;

        var names = (tagNames ?? []).Select(n => (n ?? string.Empty).Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct().ToArray();
        if (names.Length > VariantLimits.MaxTags)
            return CatalogResult.Failure<string[]>("tagNames", $"A product can have at most {VariantLimits.MaxTags} tags.");
        if (names.Any(n => n.Length > VariantLimits.MaxTextLength))
            return CatalogResult.Failure<string[]>("tagNames", $"A tag cannot exceed {VariantLimits.MaxTextLength} characters.");

        var ids = new List<int>();
        foreach (var name in names)
        {
            var tag = await specStore.GetTagByNameAsync(name, cancellationToken);
            ids.Add(tag?.Id ?? await specStore.InsertTagAsync(new ProductTag { Name = name }, cancellationToken));
        }
        await specStore.SetProductTagsAsync(product!.Id, [.. ids], cancellationToken);

        await auditLog.WriteAsync("product.tags_changed", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId, count = names.Length }, cancellationToken: cancellationToken);
        return CatalogResult.Success(names);
    }

    // ---- Specifications ----

    public async Task<CatalogResult<int[]>> GetSpecOptionIdsAsync(int vendorId, int productId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadAsync<int[]>(vendorId, productId, forWrite: false, cancellationToken);
        if (failure is not null) return failure;
        return CatalogResult.Success(OptionIds(await specStore.GetProductSpecsAsync(product!.Id, cancellationToken)));
    }

    public async Task<CatalogResult<int[]>> SetSpecOptionsAsync(
        int vendorId, int productId, int[]? optionIds, int actorCustomerId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadAsync<int[]>(vendorId, productId, forWrite: true, cancellationToken);
        if (failure is not null) return failure;

        var ids = (optionIds ?? []).Distinct().ToArray();
        if (ids.Length > VariantLimits.MaxSpecOptions)
            return CatalogResult.Failure<int[]>("optionIds", $"A product can have at most {VariantLimits.MaxSpecOptions} specification values.");
        foreach (var id in ids)
        {
            if (id <= 0 || await specStore.GetOptionAsync(id, cancellationToken) is null)
                return CatalogResult.Failure<int[]>("optionIds", "A specification value does not exist.");
        }

        await specStore.ReplaceProductOptionSpecsAsync(product!.Id, ids, cancellationToken);
        await auditLog.WriteAsync("product.specs_changed", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId, count = ids.Length }, cancellationToken: cancellationToken);
        return CatalogResult.Success(ids);
    }

    // ---- Variants ----

    public async Task<CatalogResult<ProductAttributeDetail>> GetVariantsAsync(int vendorId, int productId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadAsync<ProductAttributeDetail>(vendorId, productId, forWrite: false, cancellationToken);
        if (failure is not null) return failure;
        return CatalogResult.Success(await attributeService.GetProductAttributeDetailAsync(product!.Id, cancellationToken));
    }

    public async Task<CatalogResult<ProductAttributeDetail>> SetVariantsAsync(
        int vendorId, int productId, SaveVariantsCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var (product, failure) = await LoadAsync<ProductAttributeDetail>(vendorId, productId, forWrite: true, cancellationToken);
        if (failure is not null) return failure;

        // Saving replaces the combinations, so a hold on one of them would be lost.
        if (await inventoryStore.HasActiveReservationsAsync(productId, clock.UtcNow, cancellationToken))
            return CatalogResult.Error<ProductAttributeDetail>(CatalogErrors.ActiveReservations);

        var (errors, normalized) = await ValidateVariantsAsync(vendorId, product!, command, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<ProductAttributeDetail>(errors);

        await attributeStore.ReplaceVariantsAsync(productId, normalized!, actorCustomerId, cancellationToken);
        await auditLog.WriteAsync("product.variants_changed", actorCustomerId, entityType: "Product", entityId: productId,
            details: new { productId, vendorId, attributes = normalized!.Attributes.Count, combinations = normalized.Combinations.Count },
            cancellationToken: cancellationToken);
        return CatalogResult.Success(await attributeService.GetProductAttributeDetailAsync(productId, cancellationToken));
    }

    private async Task<(Dictionary<string, string[]> Errors, SaveVariantsCommand? Command)> ValidateVariantsAsync(
        int vendorId, Product product, SaveVariantsCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var attributes = command.Attributes ?? [];
        var combinations = command.Combinations ?? [];

        if (attributes.Count > VariantLimits.MaxAttributes)
            return Fail(errors, "attributes", $"A product can have at most {VariantLimits.MaxAttributes} attributes.");
        if (attributes.Select(a => a.ProductAttributeId).Distinct().Count() != attributes.Count)
            return Fail(errors, "attributes", "An attribute is used more than once.");

        var cleaned = new List<VariantAttributeInput>();
        foreach (var attribute in attributes)
        {
            if (await attributeStore.GetAttributeAsync(attribute.ProductAttributeId, cancellationToken) is null)
                return Fail(errors, "attributes", "An attribute does not exist.");

            var values = attribute.Values ?? [];
            if (values.Count is 0 or > VariantLimits.MaxValuesPerAttribute)
                return Fail(errors, "attributes", $"Each attribute needs 1 to {VariantLimits.MaxValuesPerAttribute} values.");

            var cleanedValues = new List<VariantValueInput>();
            foreach (var value in values)
            {
                var name = (value.Name ?? string.Empty).Trim();
                if (name.Length == 0 || name.Length > VariantLimits.MaxTextLength)
                    return Fail(errors, "attributes", $"A value name must be 1 to {VariantLimits.MaxTextLength} characters.");
                var color = string.IsNullOrWhiteSpace(value.ColorSquaresRgb) ? null : value.ColorSquaresRgb.Trim();
                if (color is not null && !ColorPattern().IsMatch(color))
                    return Fail(errors, "attributes", "A colour must look like #RRGGBB.");
                cleanedValues.Add(new VariantValueInput(name, color, value.PriceAdjustment));
            }
            if (cleanedValues.Select(v => v.Name.ToLowerInvariant()).Distinct().Count() != cleanedValues.Count)
                return Fail(errors, "attributes", "An attribute lists the same value twice.");

            cleaned.Add(new VariantAttributeInput(attribute.ProductAttributeId, attribute.IsRequired, cleanedValues));
        }

        if (cleaned.Count == 0 && combinations.Count > 0)
            return Fail(errors, "combinations", "Combinations need at least one attribute.");
        if (cleaned.Count > 0 && combinations.Count == 0)
            return Fail(errors, "combinations", "Add at least one combination.");
        if (combinations.Count > VariantLimits.MaxCombinations)
            return Fail(errors, "combinations", $"A product can have at most {VariantLimits.MaxCombinations} combinations.");

        var cleanedCombinations = new List<VariantCombinationInput>();
        var seenKeys = new HashSet<string>();
        var seenSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < combinations.Count; i++)
        {
            var combination = combinations[i];
            var label = $"Combination {i + 1}";
            var indexes = combination.ValueIndexes ?? [];
            if (indexes.Length != cleaned.Count || indexes.Select((v, a) => v < 0 || v >= cleaned[a].Values.Count).Any(bad => bad))
                return Fail(errors, "combinations", $"{label} must pick one valid value for every attribute.");
            if (!seenKeys.Add(string.Join(',', indexes)))
                return Fail(errors, "combinations", $"{label} repeats another combination.");
            if (combination.StockQuantity is < 0 or > VariantLimits.MaxStock)
                return Fail(errors, "combinations", $"{label}: stock must be between 0 and {VariantLimits.MaxStock}.");

            var sku = string.IsNullOrWhiteSpace(combination.Sku) ? null : combination.Sku.Trim();
            if (sku is not null)
            {
                if (sku.Length > VariantLimits.MaxTextLength) return Fail(errors, "combinations", $"{label}: SKU is too long.");
                if (!seenSkus.Add(sku) || await attributeStore.IsCombinationSkuTakenAsync(vendorId, sku, product.Id, cancellationToken))
                    return Fail(errors, "combinations", $"{label}: SKU “{sku}” is already used in your shop.");
            }

            var price = combination.OverriddenPrice ?? product.Price + indexes.Select((v, a) => cleaned[a].Values[v].PriceAdjustment).Sum();
            if (price <= 0) return Fail(errors, "combinations", $"{label}: the price must stay above 0.");

            cleanedCombinations.Add(combination with { ValueIndexes = indexes, Sku = sku });
        }

        return (errors, new SaveVariantsCommand(cleaned, cleanedCombinations));
    }

    private static (Dictionary<string, string[]>, SaveVariantsCommand?) Fail(Dictionary<string, string[]> errors, string field, string message)
    {
        errors[field] = [message];
        return (errors, null);
    }

    private static int[] OptionIds(IReadOnlyList<ProductSpecificationMapping> rows) =>
        rows.Where(r => r.AttributeType == SpecificationAttributeType.Option && r.SpecificationAttributeOptionId.HasValue)
            .Select(r => r.SpecificationAttributeOptionId!.Value).ToArray();

    /// <summary>The product when it belongs to the shop. Writes also need an active shop.</summary>
    private async Task<(Product? Product, CatalogResult<T>? Failure)> LoadAsync<T>(
        int vendorId, int productId, bool forWrite, CancellationToken cancellationToken)
    {
        var product = await productStore.GetAsync(productId, cancellationToken);
        if (product is null || product.VendorId != vendorId) return (null, CatalogResult.Error<T>(CatalogErrors.NotFound));
        if (!forWrite) return (product, null);

        var vendor = await vendorStore.GetAsync(vendorId, cancellationToken);
        if (vendor is null) return (null, CatalogResult.Error<T>(CatalogErrors.NotFound));
        if (!vendor.Active) return (null, CatalogResult.Error<T>(CatalogErrors.Forbidden));
        return (product, null);
    }
}
