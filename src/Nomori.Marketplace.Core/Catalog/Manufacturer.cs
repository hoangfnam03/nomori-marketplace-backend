namespace Nomori.Marketplace.Core.Catalog;

public sealed class Manufacturer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int PictureId { get; set; }
    public bool Published { get; set; }
    public bool Deleted { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public interface IManufacturerStore
{
    Task<Manufacturer?> GetAsync(int id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Manufacturer> Items, int TotalCount)> GetPagedAsync(ManufacturerQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<Manufacturer>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken);

    /// <summary>Non-deleted products mapped to the manufacturer.</summary>
    Task<int> CountProductsAsync(int id, CancellationToken cancellationToken);

    /// <summary>Case-insensitive name check against non-deleted manufacturers.</summary>
    Task<bool> NameExistsAsync(string name, int? excludeId, CancellationToken cancellationToken);

    Task<int> InsertAsync(Manufacturer manufacturer, CancellationToken cancellationToken);
    Task UpdateAsync(Manufacturer manufacturer, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
}

public interface IManufacturerService
{
    Task<Manufacturer?> GetAsync(int id, CancellationToken cancellationToken);
    Task<PagedResult<Manufacturer>> GetListAsync(ManufacturerQuery query, CancellationToken cancellationToken);
    Task<CatalogResult<Manufacturer>> CreateAsync(CreateManufacturerCommand command, CancellationToken cancellationToken);
    Task<CatalogResult<Manufacturer>> UpdateAsync(UpdateManufacturerCommand command, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteAsync(int id, CancellationToken cancellationToken);
}
