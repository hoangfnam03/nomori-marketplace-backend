using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Media;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class CategoryService(ICategoryStore categoryStore, IMediaStore mediaStore, IClock clock) : ICategoryService
{
    public Task<Category?> GetAsync(int id, CancellationToken cancellationToken) =>
        categoryStore.GetAsync(id, cancellationToken);

    public async Task<Category?> GetPublicAsync(int id, CancellationToken cancellationToken)
    {
        var hierarchy = new CategoryHierarchy(await categoryStore.GetAllAsync(cancellationToken));
        var category = hierarchy.Find(id);
        return category is not null && hierarchy.IsPublishedChain(category) ? category : null;
    }

    public async Task<PagedResult<Category>> GetListAsync(CategoryQuery query, CancellationToken cancellationToken)
    {
        var (items, total) = await categoryStore.GetPagedAsync(query, cancellationToken);
        return new PagedResult<Category>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<CategoryTreeNode>> GetTreeAsync(CancellationToken cancellationToken)
    {
        // Only published categories are loaded, so a category under an unpublished parent is never reached from the root.
        var all = await categoryStore.GetAllPublishedAsync(cancellationToken);
        return BuildTree(all, 0);
    }

    public async Task<IReadOnlyList<CategoryTreeNode>> GetAdminTreeAsync(CancellationToken cancellationToken) =>
        BuildTree(await categoryStore.GetAllAsync(cancellationToken), 0);

    public async Task<IReadOnlyList<SelectableCategory>> GetSelectableForSellersAsync(CancellationToken cancellationToken)
    {
        var all = await categoryStore.GetAllAsync(cancellationToken);
        var hierarchy = new CategoryHierarchy(all);
        return all
            .Where(c => !c.RestrictFromVendors && hierarchy.IsPublishedChain(c))
            .Select(c => new SelectableCategory(c.Id, c.Name, c.ParentCategoryId, hierarchy.Path(c)))
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<CatalogResult<Category>> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var errors = ValidateName(command.Name);
        await ValidateParentAsync(null, command.ParentCategoryId, errors, cancellationToken);
        if (!errors.ContainsKey("name") && !errors.ContainsKey("parentCategoryId")
            && await categoryStore.NameExistsAsync(command.Name.Trim(), command.ParentCategoryId, null, cancellationToken))
            errors["name"] = ["A category with this name already exists under the same parent."];
        await MediaAttachment.ValidateAsync(mediaStore, command.PictureId, MediaPurpose.Category, null, errors, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<Category>(errors);

        var now = clock.UtcNow;
        var category = new Category
        {
            Name = command.Name.Trim(),
            Description = NullIfBlank(command.Description),
            ParentCategoryId = command.ParentCategoryId,
            PictureId = command.PictureId,
            ShowOnHomepage = command.ShowOnHomepage,
            Published = command.Published,
            RestrictFromVendors = command.RestrictFromVendors,
            DisplayOrder = command.DisplayOrder,
            CreatedOnUtc = now,
            UpdatedOnUtc = now
        };
        category.Id = await categoryStore.InsertAsync(category, cancellationToken);
        return CatalogResult.Success(category);
    }

    public async Task<CatalogResult<Category>> UpdateAsync(UpdateCategoryCommand command, CancellationToken cancellationToken)
    {
        var existing = await categoryStore.GetAsync(command.Id, cancellationToken);
        if (existing is null) return CatalogResult.Error<Category>(CatalogErrors.NotFound);

        var errors = ValidateName(command.Name);
        // An unchanged parent cannot introduce a loop, so legacy data is not re-judged.
        if (command.ParentCategoryId != existing.ParentCategoryId)
            await ValidateParentAsync(existing.Id, command.ParentCategoryId, errors, cancellationToken);

        // Legacy duplicates are tolerated until the name or parent actually changes.
        var nameOrParentChanged = command.ParentCategoryId != existing.ParentCategoryId
            || !string.Equals(command.Name.Trim(), existing.Name, StringComparison.OrdinalIgnoreCase);
        if (nameOrParentChanged && !errors.ContainsKey("name") && !errors.ContainsKey("parentCategoryId")
            && await categoryStore.NameExistsAsync(command.Name.Trim(), command.ParentCategoryId, existing.Id, cancellationToken))
            errors["name"] = ["A category with this name already exists under the same parent."];

        if (command.PictureId != existing.PictureId)
            await MediaAttachment.ValidateAsync(mediaStore, command.PictureId, MediaPurpose.Category, null, errors, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<Category>(errors);

        existing.Name = command.Name.Trim();
        existing.Description = NullIfBlank(command.Description);
        existing.ParentCategoryId = command.ParentCategoryId;
        existing.PictureId = command.PictureId;
        existing.ShowOnHomepage = command.ShowOnHomepage;
        existing.Published = command.Published;
        existing.RestrictFromVendors = command.RestrictFromVendors;
        existing.DisplayOrder = command.DisplayOrder;
        existing.UpdatedOnUtc = clock.UtcNow;
        await categoryStore.UpdateAsync(existing, cancellationToken);
        return CatalogResult.Success(existing);
    }

    public async Task<CatalogResult<bool>> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var existing = await categoryStore.GetAsync(id, cancellationToken);
        if (existing is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);
        if (await categoryStore.CountChildrenAsync(id, cancellationToken) > 0)
            return CatalogResult.Error<bool>(CatalogErrors.CategoryHasChildren);
        if (await categoryStore.CountProductsAsync(id, cancellationToken) > 0)
            return CatalogResult.Error<bool>(CatalogErrors.CategoryInUse);

        await categoryStore.DeleteAsync(id, cancellationToken);
        return CatalogResult.Success(true);
    }

    /// <summary>The parent must be root (0) or an existing category, and never the category itself or one of its descendants.</summary>
    private async Task ValidateParentAsync(int? categoryId, int parentId, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (parentId == 0) return;
        if (parentId < 0) { errors["parentCategoryId"] = ["Parent category does not exist."]; return; }
        if (parentId == categoryId) { errors["parentCategoryId"] = ["A category cannot be its own parent."]; return; }

        var hierarchy = new CategoryHierarchy(await categoryStore.GetAllAsync(cancellationToken));
        if (hierarchy.Find(parentId) is null) { errors["parentCategoryId"] = ["Parent category does not exist."]; return; }
        if (categoryId is { } id && hierarchy.DescendantIds(id).Contains(parentId))
            errors["parentCategoryId"] = ["A category cannot be moved under one of its own subcategories."];
    }

    private static List<CategoryTreeNode> BuildTree(IReadOnlyList<Category> all, int parentId) =>
        all
            .Where(c => c.ParentCategoryId == parentId)
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryTreeNode
            {
                Id = c.Id,
                Name = c.Name,
                ParentCategoryId = c.ParentCategoryId,
                DisplayOrder = c.DisplayOrder,
                Published = c.Published,
                RestrictFromVendors = c.RestrictFromVendors,
                Children = BuildTree(all, c.Id)
            })
            .ToList();

    private static Dictionary<string, string[]> ValidateName(string? name)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Category name is required."];
        else if (name.Trim().Length > 400) errors["name"] = ["Category name cannot exceed 400 characters."];
        return errors;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
