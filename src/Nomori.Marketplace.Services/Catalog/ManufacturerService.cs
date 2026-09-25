using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Catalog;

public sealed class ManufacturerService(IManufacturerStore manufacturerStore, IClock clock) : IManufacturerService
{
    public Task<Manufacturer?> GetAsync(int id, CancellationToken cancellationToken) =>
        manufacturerStore.GetAsync(id, cancellationToken);

    public async Task<PagedResult<Manufacturer>> GetListAsync(ManufacturerQuery query, CancellationToken cancellationToken)
    {
        var (items, total) = await manufacturerStore.GetPagedAsync(query, cancellationToken);
        return new PagedResult<Manufacturer>(items, total, query.Page, query.PageSize);
    }

    public async Task<CatalogResult<Manufacturer>> CreateAsync(CreateManufacturerCommand command, CancellationToken cancellationToken)
    {
        var errors = ValidateName(command.Name);
        if (errors.Count > 0) return CatalogResult.Failure<Manufacturer>(errors);

        var now = clock.UtcNow;
        var manufacturer = new Manufacturer
        {
            Name = command.Name.Trim(),
            Description = NullIfBlank(command.Description),
            PictureId = command.PictureId,
            Published = command.Published,
            DisplayOrder = command.DisplayOrder,
            CreatedOnUtc = now,
            UpdatedOnUtc = now
        };
        manufacturer.Id = await manufacturerStore.InsertAsync(manufacturer, cancellationToken);
        return CatalogResult.Success(manufacturer);
    }

    public async Task<CatalogResult<Manufacturer>> UpdateAsync(UpdateManufacturerCommand command, CancellationToken cancellationToken)
    {
        var existing = await manufacturerStore.GetAsync(command.Id, cancellationToken);
        if (existing is null) return CatalogResult.Failure<Manufacturer>("id", "Manufacturer not found.");

        var errors = ValidateName(command.Name);
        if (errors.Count > 0) return CatalogResult.Failure<Manufacturer>(errors);

        existing.Name = command.Name.Trim();
        existing.Description = NullIfBlank(command.Description);
        existing.PictureId = command.PictureId;
        existing.Published = command.Published;
        existing.DisplayOrder = command.DisplayOrder;
        existing.UpdatedOnUtc = clock.UtcNow;
        await manufacturerStore.UpdateAsync(existing, cancellationToken);
        return CatalogResult.Success(existing);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var existing = await manufacturerStore.GetAsync(id, cancellationToken);
        if (existing is null) return false;
        await manufacturerStore.DeleteAsync(id, cancellationToken);
        return true;
    }

    private static Dictionary<string, string[]> ValidateName(string name)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Manufacturer name is required."];
        else if (name.Length > 400) errors["name"] = ["Manufacturer name cannot exceed 400 characters."];
        return errors;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
