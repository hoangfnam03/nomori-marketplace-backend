using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Services.Tests;

/// <summary>
/// A stock service with the same rules as the real one where checkout depends on them: reservations count against what others can take,
/// commit takes the stock or changes nothing, products without tracking hold nothing, and returns go back only for tracked products.
/// </summary>
internal sealed class FakeStockService : IInventoryService
{
    private readonly Dictionary<string, List<(int ProductId, int? CombinationId, int Quantity)>> holds = [];

    public Dictionary<int, int> OnHand { get; } = [];
    public HashSet<int> Untracked { get; } = [];
    public List<(string Reference, int ProductId, int? CombinationId, int Quantity)> Returns { get; } = [];

    /// <summary>When set, returning stock fails (the lines that could be put back still are).</summary>
    public bool FailReturns { get; set; }

    public int ActiveHolds => holds.Count;

    public Task<CatalogResult<bool>> ReserveAsync(string reference, int productId, int? combinationId, int quantity, int? minutes, CancellationToken cancellationToken)
    {
        if (quantity is < 1 or > InventoryLimits.MaxReservationQuantity)
            return Task.FromResult(CatalogResult.Failure<bool>("quantity", "The quantity must be between 1 and 1000."));
        if (Untracked.Contains(productId)) return Task.FromResult(CatalogResult.Success(true));

        var reservedByOthers = holds.Where(h => h.Key != reference).SelectMany(h => h.Value).Where(h => h.ProductId == productId).Sum(h => h.Quantity);
        if (!StockRules.CanReserve(OnHand.GetValueOrDefault(productId), reservedByOthers, quantity))
            return Task.FromResult(CatalogResult.Error<bool>(CatalogErrors.InsufficientStock));

        if (!holds.TryGetValue(reference, out var list)) holds[reference] = list = [];
        list.RemoveAll(h => h.ProductId == productId && h.CombinationId == combinationId);
        list.Add((productId, combinationId, quantity));
        return Task.FromResult(CatalogResult.Success(true));
    }

    public Task<int> ReleaseAsync(string reference, CancellationToken cancellationToken) =>
        Task.FromResult(holds.Remove(reference, out var list) ? list.Count : 0);

    public Task<CatalogResult<bool>> CommitAsync(string reference, CancellationToken cancellationToken)
    {
        if (!holds.TryGetValue(reference, out var list) || list.Count == 0) return Task.FromResult(CatalogResult.Error<bool>(CatalogErrors.ReservationExpired));
        if (list.Any(h => OnHand.GetValueOrDefault(h.ProductId) < h.Quantity)) return Task.FromResult(CatalogResult.Error<bool>(CatalogErrors.InsufficientStock));

        foreach (var hold in list) OnHand[hold.ProductId] = OnHand.GetValueOrDefault(hold.ProductId) - hold.Quantity;
        holds.Remove(reference);
        return Task.FromResult(CatalogResult.Success(true));
    }

    public Task<CatalogResult<bool>> ReturnToStockAsync(string reference, IReadOnlyList<StockReturnLine> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines.Where(l => !Untracked.Contains(l.ProductId)))
        {
            if (FailReturns) continue;
            OnHand[line.ProductId] = OnHand.GetValueOrDefault(line.ProductId) + line.Quantity;
            Returns.Add((reference, line.ProductId, line.CombinationId, line.Quantity));
        }
        return Task.FromResult(FailReturns ? CatalogResult.Error<bool>(CatalogErrors.NotFound) : CatalogResult.Success(true));
    }

    public Task<Availability?> GetAvailabilityAsync(int productId, CancellationToken cancellationToken)
    {
        var reserved = holds.SelectMany(h => h.Value).Where(h => h.ProductId == productId).Sum(h => h.Quantity);
        return Task.FromResult<Availability?>(new Availability(!Untracked.Contains(productId), StockRules.Available(OnHand.GetValueOrDefault(productId), reserved), new Dictionary<int, int>()));
    }

    public Task<CatalogResult<InventoryOverview>> GetOverviewForVendorAsync(int vendorId, int productId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CatalogResult<InventoryOverview>> AdjustForVendorAsync(int vendorId, int productId, AdjustStockCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CatalogResult<InventoryOverview>> SetSettingsForVendorAsync(int vendorId, int productId, bool trackInventory, int lowStockThreshold, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CatalogResult<PagedResult<StockMovement>>> GetMovementsForVendorAsync(int vendorId, int productId, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
}
