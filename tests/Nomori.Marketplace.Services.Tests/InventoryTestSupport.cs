using Nomori.Marketplace.Core.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

/// <summary>
/// In-memory inventory store that follows the same steps as the SQL one (read the stock, ask <see cref="StockRules"/>, write),
/// without the locking. It works on the fake product and attribute stores so a test sees one consistent state.
/// </summary>
internal sealed class FakeInventoryStore(FakeProductStore products, FakeAttributeStore? attributes = null) : IInventoryStore
{
    public sealed class Hold
    {
        public string Reference { get; init; } = string.Empty;
        public int ProductId { get; init; }
        public int? CombinationId { get; init; }
        public int Quantity { get; set; }
        public DateTime ExpiresOnUtc { get; set; }
        public int Status { get; set; }
    }

    private int nextMovementId = 1;

    public List<StockMovement> Movements { get; } = [];
    public List<Hold> Holds { get; } = [];

    private Product? ProductOf(int id) => products.Products.FirstOrDefault(p => p.Id == id && !p.Deleted);

    private ProductAttributeCombination? CombinationOf(int productId, int id) =>
        attributes?.Combinations.FirstOrDefault(c => c.Id == id && c.ProductId == productId);

    private int? OnHand(int productId, int? combinationId) =>
        combinationId is { } cid ? CombinationOf(productId, cid)?.StockQuantity : ProductOf(productId)?.StockQuantity;

    private int Reserved(int productId, int? combinationId, string? exclude, DateTime now) =>
        Holds.Where(h => h.ProductId == productId && h.Status == 0 && h.ExpiresOnUtc > now
                         && (combinationId is null || h.CombinationId == combinationId) && (exclude is null || h.Reference != exclude))
            .Sum(h => h.Quantity);

    public Task<StockLevel?> GetLevelAsync(int productId, int? combinationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var onHand = OnHand(productId, combinationId);
        return Task.FromResult(onHand is null ? null : new StockLevel(onHand.Value, Reserved(productId, combinationId, null, nowUtc)));
    }

    public Task<IReadOnlyDictionary<int, StockLevel>> GetCombinationLevelsAsync(int productId, DateTime nowUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, StockLevel>>((attributes?.Combinations ?? []).Where(c => c.ProductId == productId)
            .ToDictionary(c => c.Id, c => new StockLevel(c.StockQuantity, Reserved(productId, c.Id, null, nowUtc))));

    public Task<InventoryChange> AdjustAsync(StockAdjustment adjustment, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var onHand = OnHand(adjustment.ProductId, adjustment.CombinationId);
        if (onHand is null) return Task.FromResult(new InventoryChange(InventoryOutcome.NotFound));
        if (!StockRules.CanAdjust(onHand.Value, Reserved(adjustment.ProductId, adjustment.CombinationId, null, nowUtc), adjustment.Delta))
            return Task.FromResult(new InventoryChange(InventoryOutcome.Insufficient, onHand.Value));

        Apply(adjustment.ProductId, adjustment.CombinationId, adjustment.Delta);
        Movements.Add(new StockMovement
        {
            Id = nextMovementId++, ProductId = adjustment.ProductId, CombinationId = adjustment.CombinationId, Delta = adjustment.Delta,
            QuantityAfter = onHand.Value + adjustment.Delta, Reason = adjustment.Reason, Reference = adjustment.Reference, Note = adjustment.Note,
            ActorCustomerId = adjustment.ActorCustomerId, CreatedOnUtc = nowUtc
        });
        return Task.FromResult(new InventoryChange(InventoryOutcome.Ok, onHand.Value + adjustment.Delta));
    }

    private void Apply(int productId, int? combinationId, int delta)
    {
        if (combinationId is { } cid) CombinationOf(productId, cid)!.StockQuantity += delta;
        ProductOf(productId)!.StockQuantity += delta;
    }

    public Task RecordMovementAsync(StockMovement movement, CancellationToken cancellationToken)
    {
        movement.Id = nextMovementId++;
        Movements.Add(movement);
        return Task.CompletedTask;
    }

    public Task<InventoryChange> ReserveAsync(StockReservationRequest request, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var onHand = OnHand(request.ProductId, request.CombinationId);
        if (onHand is null) return Task.FromResult(new InventoryChange(InventoryOutcome.NotFound));
        if (!StockRules.CanReserve(onHand.Value, Reserved(request.ProductId, request.CombinationId, request.Reference, nowUtc), request.Quantity))
            return Task.FromResult(new InventoryChange(InventoryOutcome.Insufficient, onHand.Value));

        var existing = Holds.FirstOrDefault(h => h.Reference == request.Reference && h.ProductId == request.ProductId
                                                 && h.CombinationId == request.CombinationId && h.Status == 0);
        if (existing is null)
        {
            Holds.Add(new Hold
            {
                Reference = request.Reference, ProductId = request.ProductId, CombinationId = request.CombinationId,
                Quantity = request.Quantity, ExpiresOnUtc = request.ExpiresOnUtc
            });
        }
        else
        {
            existing.Quantity = request.Quantity;
            existing.ExpiresOnUtc = request.ExpiresOnUtc;
        }
        return Task.FromResult(new InventoryChange(InventoryOutcome.Ok, onHand.Value));
    }

    public Task<int> ReleaseAsync(string reference, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var active = Holds.Where(h => h.Reference == reference && h.Status == 0).ToList();
        foreach (var hold in active) hold.Status = 2;
        return Task.FromResult(active.Count);
    }

    public Task<InventoryChange> CommitAsync(string reference, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var active = Holds.Where(h => h.Reference == reference && h.Status == 0).ToList();
        if (active.Count == 0 || active.Any(h => h.ExpiresOnUtc <= nowUtc)) return Task.FromResult(new InventoryChange(InventoryOutcome.Expired));
        // All or nothing, like the transaction.
        if (active.Any(h => (OnHand(h.ProductId, h.CombinationId) ?? 0) < h.Quantity)) return Task.FromResult(new InventoryChange(InventoryOutcome.Insufficient));

        foreach (var hold in active)
        {
            var before = OnHand(hold.ProductId, hold.CombinationId)!.Value;
            Apply(hold.ProductId, hold.CombinationId, -hold.Quantity);
            Movements.Add(new StockMovement
            {
                Id = nextMovementId++, ProductId = hold.ProductId, CombinationId = hold.CombinationId, Delta = -hold.Quantity,
                QuantityAfter = before - hold.Quantity, Reason = StockReasons.Sale, Reference = reference, CreatedOnUtc = nowUtc
            });
            hold.Status = 1;
        }
        return Task.FromResult(new InventoryChange(InventoryOutcome.Ok));
    }

    public Task<bool> HasActiveReservationsAsync(int productId, DateTime nowUtc, CancellationToken cancellationToken) =>
        Task.FromResult(Holds.Any(h => h.ProductId == productId && h.Status == 0 && h.ExpiresOnUtc > nowUtc));

    public Task<(IReadOnlyList<StockMovement> Items, int TotalCount)> GetMovementsAsync(int productId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var all = Movements.Where(m => m.ProductId == productId).OrderByDescending(m => m.Id).ToList();
        return Task.FromResult<(IReadOnlyList<StockMovement>, int)>((all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), all.Count));
    }

    public Task SetSettingsAsync(int productId, bool trackInventory, int lowStockThreshold, CancellationToken cancellationToken)
    {
        var product = ProductOf(productId)!;
        product.TrackInventory = trackInventory;
        product.LowStockThreshold = lowStockThreshold;
        return Task.CompletedTask;
    }
}
