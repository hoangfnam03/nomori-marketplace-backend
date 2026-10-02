using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Api.Modules.Catalog;

/// <summary>
/// Stock of the products of one shop, for members of that shop. Anyone else gets 404.
/// Reserving, releasing and committing stock are internal operations of the cart and checkout and have no route yet.
/// </summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/products/{id:int}")]
[Authorize]
public sealed class VendorInventoryController(
    IInventoryService inventoryService,
    IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet("inventory")]
    public async Task<IActionResult> GetInventory(int vendorId, int id, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var result = await inventoryService.GetOverviewForVendorAsync(vendorId, id, cancellationToken);
        return result.Succeeded ? Ok(InventoryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>Changes the stock by a signed amount and records why. A product with variants needs a combination id.</summary>
    [HttpPost("stock-adjustments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdjustStock(int vendorId, int id, AdjustStockRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await inventoryService.AdjustForVendorAsync(
            vendorId, id, new AdjustStockCommand(request.CombinationId, request.Delta, request.Reason, request.Note), caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(InventoryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("inventory-settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSettings(int vendorId, int id, InventorySettingsRequest request, CancellationToken cancellationToken)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        var result = await inventoryService.SetSettingsForVendorAsync(
            vendorId, id, request.TrackInventory, request.LowStockThreshold, caller.CustomerId!.Value, cancellationToken);
        return result.Succeeded ? Ok(InventoryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpGet("stock-movements")]
    public async Task<IActionResult> GetMovements(
        int vendorId, int id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (await RequireMemberAsync(vendorId, cancellationToken) is null) return NotFound();
        var result = await inventoryService.GetMovementsForVendorAsync(vendorId, id, page, pageSize, cancellationToken);
        if (!result.Succeeded) return this.ToFailure(result);

        var paged = result.Value!;
        return Ok(new CatalogPagedResponse<StockMovementResponse>(
            paged.Items.Select(StockMovementResponse.From).ToList(), paged.TotalCount, paged.Page, paged.PageSize, paged.TotalPages));
    }

    /// <summary>The caller, only when they are a member of the shop in the route. The shop id is never taken from the body.</summary>
    private async Task<VendorCaller?> RequireMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId) ? caller : null;
    }
}

public sealed record AdjustStockRequest(int? CombinationId, int Delta, string? Reason, string? Note = null);

public sealed record InventorySettingsRequest(bool TrackInventory, int LowStockThreshold);

public sealed record StockLevelResponse(int OnHand, int Reserved, int Available)
{
    public static StockLevelResponse From(StockLevel level) => new(level.OnHand, level.Reserved, level.Available);
}

public sealed record CombinationStockResponse(int Id, string? Sku, string AttributesJson, StockLevelResponse Stock);

public sealed record InventoryResponse(
    bool TrackInventory, int LowStockThreshold, bool IsLowStock, StockLevelResponse Stock, IReadOnlyList<CombinationStockResponse> Combinations)
{
    public static InventoryResponse From(InventoryOverview overview) => new(
        overview.TrackInventory, overview.LowStockThreshold,
        StockRules.IsLow(overview.TrackInventory, overview.Product.OnHand, overview.LowStockThreshold),
        StockLevelResponse.From(overview.Product),
        overview.Combinations.Select(c => new CombinationStockResponse(c.Id, c.Sku, c.AttributesJson, StockLevelResponse.From(c.Level))).ToList());
}

public sealed record StockMovementResponse(
    int Id, int? CombinationId, int Delta, int QuantityAfter, string Reason, string? Reference, string? Note, DateTime CreatedOnUtc)
{
    public static StockMovementResponse From(StockMovement m) => new(
        m.Id, m.CombinationId, m.Delta, m.QuantityAfter, m.Reason, m.Reference, m.Note, DateTime.SpecifyKind(m.CreatedOnUtc, DateTimeKind.Utc));
}
