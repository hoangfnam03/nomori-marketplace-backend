using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class TaxonomyService(ICategoryStore categoryStore, IManufacturerStore manufacturerStore) : ITaxonomyService
{
    public async Task<CatalogResult<TaxonomySelection>> ValidateSelectionAsync(
        int[] categoryIds, int[] manufacturerIds, TaxonomyAudience audience, CancellationToken cancellationToken)
    {
        var categories = categoryIds.Distinct().ToArray();
        var manufacturers = manufacturerIds.Distinct().ToArray();
        var errors = new Dictionary<string, string[]>();

        if (categories.Length > TaxonomyLimits.MaxCategoriesPerProduct)
            errors["categoryIds"] = [$"A product can have at most {TaxonomyLimits.MaxCategoriesPerProduct} categories."];
        else if (categories.Length > 0)
            await CheckCategoriesAsync(categories, audience, errors, cancellationToken);

        if (manufacturers.Length > TaxonomyLimits.MaxManufacturersPerProduct)
            errors["manufacturerIds"] = [$"A product can have at most {TaxonomyLimits.MaxManufacturersPerProduct} manufacturers."];
        else if (manufacturers.Length > 0)
            await CheckManufacturersAsync(manufacturers, audience, errors, cancellationToken);

        return errors.Count > 0
            ? CatalogResult.Failure<TaxonomySelection>(errors)
            : CatalogResult.Success(new TaxonomySelection(categories, manufacturers));
    }

    private async Task CheckCategoriesAsync(
        int[] ids, TaxonomyAudience audience, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var hierarchy = new CategoryHierarchy(await categoryStore.GetAllAsync(cancellationToken));
        var problems = new List<string>();
        foreach (var id in ids)
        {
            var category = id > 0 ? hierarchy.Find(id) : null;
            if (category is null) { problems.Add($"Category {id} does not exist."); continue; }
            if (audience != TaxonomyAudience.Seller) continue;

            if (category.RestrictFromVendors || !hierarchy.IsPublishedChain(category))
                problems.Add($"Category {id} cannot be used by sellers.");
        }

        if (problems.Count > 0) errors["categoryIds"] = [.. problems];
    }

    private async Task CheckManufacturersAsync(
        int[] ids, TaxonomyAudience audience, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var found = (await manufacturerStore.GetByIdsAsync(ids.Where(i => i > 0).ToArray(), cancellationToken))
            .ToDictionary(m => m.Id);
        var problems = new List<string>();
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var manufacturer)) { problems.Add($"Manufacturer {id} does not exist."); continue; }
            if (audience == TaxonomyAudience.Seller && !manufacturer.Published)
                problems.Add($"Manufacturer {id} cannot be used by sellers.");
        }

        if (problems.Count > 0) errors["manufacturerIds"] = [.. problems];
    }
}
