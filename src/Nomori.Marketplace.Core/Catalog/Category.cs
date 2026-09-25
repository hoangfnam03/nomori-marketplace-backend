namespace Nomori.Marketplace.Core.Catalog;

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ParentCategoryId { get; set; }
    public int PictureId { get; set; }
    public bool ShowOnHomepage { get; set; }
    public bool Published { get; set; }
    public bool Deleted { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public sealed class CategoryTreeNode
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ParentCategoryId { get; set; }
    public int DisplayOrder { get; set; }
    public IReadOnlyList<CategoryTreeNode> Children { get; set; } = [];
}

public interface ICategoryStore
{
    Task<Category?> GetAsync(int id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Category> Items, int TotalCount)> GetPagedAsync(CategoryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<Category>> GetAllPublishedAsync(CancellationToken cancellationToken);
    Task<int> InsertAsync(Category category, CancellationToken cancellationToken);
    Task UpdateAsync(Category category, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
}

public interface ICategoryService
{
    Task<Category?> GetAsync(int id, CancellationToken cancellationToken);
    Task<PagedResult<Category>> GetListAsync(CategoryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<CategoryTreeNode>> GetTreeAsync(CancellationToken cancellationToken);
    Task<CatalogResult<Category>> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken);
    Task<CatalogResult<Category>> UpdateAsync(UpdateCategoryCommand command, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
}
