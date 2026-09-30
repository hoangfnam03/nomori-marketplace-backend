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

    /// <summary>When true, sellers cannot attach products to this category. Administrators still can.</summary>
    public bool RestrictFromVendors { get; set; }

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
    public bool Published { get; set; } = true;
    public bool RestrictFromVendors { get; set; }
    public IReadOnlyList<CategoryTreeNode> Children { get; set; } = [];
}

public interface ICategoryStore
{
    Task<Category?> GetAsync(int id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Category> Items, int TotalCount)> GetPagedAsync(CategoryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<Category>> GetAllPublishedAsync(CancellationToken cancellationToken);

    /// <summary>Every non-deleted category, published or not, for tree validation and the admin tree.</summary>
    Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken);

    Task<int> CountChildrenAsync(int id, CancellationToken cancellationToken);

    /// <summary>Non-deleted products mapped to the category.</summary>
    Task<int> CountProductsAsync(int id, CancellationToken cancellationToken);

    /// <summary>Case-insensitive sibling name check against non-deleted categories.</summary>
    Task<bool> NameExistsAsync(string name, int parentCategoryId, int? excludeId, CancellationToken cancellationToken);
    Task<int> InsertAsync(Category category, CancellationToken cancellationToken);
    Task UpdateAsync(Category category, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
}

public interface ICategoryService
{
    Task<Category?> GetAsync(int id, CancellationToken cancellationToken);
    /// <summary>The category only when it and all its ancestors are published, otherwise null.</summary>
    Task<Category?> GetPublicAsync(int id, CancellationToken cancellationToken);
    Task<PagedResult<Category>> GetListAsync(CategoryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<CategoryTreeNode>> GetTreeAsync(CancellationToken cancellationToken);

    /// <summary>Whole tree including unpublished categories, for administrators.</summary>
    Task<IReadOnlyList<CategoryTreeNode>> GetAdminTreeAsync(CancellationToken cancellationToken);

    /// <summary>Flat list, with full path, of the categories a seller may attach products to.</summary>
    Task<IReadOnlyList<SelectableCategory>> GetSelectableForSellersAsync(CancellationToken cancellationToken);
    Task<CatalogResult<Category>> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken);
    Task<CatalogResult<Category>> UpdateAsync(UpdateCategoryCommand command, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteAsync(int id, CancellationToken cancellationToken);
}

public sealed record SelectableCategory(int Id, string Name, int ParentCategoryId, string Path);
