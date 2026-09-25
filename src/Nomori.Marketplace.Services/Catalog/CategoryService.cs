using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class CategoryService(ICategoryStore categoryStore, IClock clock) : ICategoryService
{
    public Task<Category?> GetAsync(int id, CancellationToken cancellationToken) =>
        categoryStore.GetAsync(id, cancellationToken);

    public async Task<PagedResult<Category>> GetListAsync(CategoryQuery query, CancellationToken cancellationToken)
    {
        var (items, total) = await categoryStore.GetPagedAsync(query, cancellationToken);
        return new PagedResult<Category>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<CategoryTreeNode>> GetTreeAsync(CancellationToken cancellationToken)
    {
        var all = await categoryStore.GetAllPublishedAsync(cancellationToken);
        return BuildTree(all, 0);
    }

    public async Task<CatalogResult<Category>> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var errors = ValidateName(command.Name);
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
        if (existing is null) return CatalogResult.Failure<Category>("id", "Category not found.");

        var errors = ValidateName(command.Name);
        if (errors.Count > 0) return CatalogResult.Failure<Category>(errors);

        existing.Name = command.Name.Trim();
        existing.Description = NullIfBlank(command.Description);
        existing.ParentCategoryId = command.ParentCategoryId;
        existing.PictureId = command.PictureId;
        existing.ShowOnHomepage = command.ShowOnHomepage;
        existing.Published = command.Published;
        existing.DisplayOrder = command.DisplayOrder;
        existing.UpdatedOnUtc = clock.UtcNow;
        await categoryStore.UpdateAsync(existing, cancellationToken);
        return CatalogResult.Success(existing);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var existing = await categoryStore.GetAsync(id, cancellationToken);
        if (existing is null) return false;
        await categoryStore.DeleteAsync(id, cancellationToken);
        return true;
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
                Children = BuildTree(all, c.Id)
            })
            .ToList();

    private static Dictionary<string, string[]> ValidateName(string name)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Category name is required."];
        else if (name.Length > 400) errors["name"] = ["Category name cannot exceed 400 characters."];
        return errors;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
