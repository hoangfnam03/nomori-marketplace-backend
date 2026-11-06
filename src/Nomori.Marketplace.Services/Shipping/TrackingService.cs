using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Shipping;

/// <summary>
/// Carriers and the journey of a parcel (F20-B). The shop adds events while the shop order is shipped; the customer, the shop and the
/// platform read the same view. A shop order the caller may not see does not exist for the caller.
/// </summary>
public sealed class TrackingService(
    ITrackingStore store,
    IOrderStore orders,
    IAuditLogService auditLog,
    IClock clock) : ITrackingService
{
    // ---- Carriers ----

    public Task<IReadOnlyList<Carrier>> GetCarriersAsync(bool activeOnly, CancellationToken cancellationToken) =>
        store.ListCarriersAsync(activeOnly, cancellationToken);

    public async Task<CatalogResult<Carrier>> CreateCarrierAsync(SaveCarrierCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = Validate(command, out var name, out var template);
        if (errors.Count > 0) return CatalogResult.Failure<Carrier>(errors);
        if (await store.CountCarriersAsync(cancellationToken) >= TrackingLimits.MaxCarriers) return CatalogResult.Error<Carrier>(TrackingErrors.CarrierLimit);
        if (await store.CarrierNameExistsAsync(name, 0, cancellationToken)) return CatalogResult.Error<Carrier>(TrackingErrors.CarrierNameExists);

        var now = clock.UtcNow;
        var carrier = new Carrier { Name = name, TrackingUrlTemplate = template, Active = command.Active, DisplayOrder = command.DisplayOrder, CreatedOnUtc = now, UpdatedOnUtc = now };
        carrier.Id = await store.InsertCarrierAsync(carrier, cancellationToken);
        await auditLog.WriteAsync("carrier.created", actorCustomerId, entityType: "Carrier", entityId: carrier.Id, details: new { carrier.Name }, cancellationToken: cancellationToken);
        return CatalogResult.Success(carrier);
    }

    public async Task<CatalogResult<Carrier>> UpdateCarrierAsync(int id, SaveCarrierCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = Validate(command, out var name, out var template);
        if (errors.Count > 0) return CatalogResult.Failure<Carrier>(errors);
        var carrier = await store.GetCarrierAsync(id, cancellationToken);
        if (carrier is null) return CatalogResult.Error<Carrier>(CatalogErrors.NotFound);
        if (await store.CarrierNameExistsAsync(name, id, cancellationToken)) return CatalogResult.Error<Carrier>(TrackingErrors.CarrierNameExists);

        carrier.Name = name;
        carrier.TrackingUrlTemplate = template;
        carrier.Active = command.Active;
        carrier.DisplayOrder = command.DisplayOrder;
        carrier.UpdatedOnUtc = clock.UtcNow;
        await store.UpdateCarrierAsync(carrier, cancellationToken);
        await auditLog.WriteAsync("carrier.updated", actorCustomerId, entityType: "Carrier", entityId: id, details: new { carrier.Name, carrier.Active }, cancellationToken: cancellationToken);
        return CatalogResult.Success(carrier);
    }

    public async Task<CatalogResult<bool>> DeleteCarrierAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        // Shop orders keep the carrier's name as text, so nothing refers to the row: deleting only takes the name off the pick list and the link.
        if (!await store.DeleteCarrierAsync(id, cancellationToken)) return CatalogResult.Error<bool>(CatalogErrors.NotFound);
        await auditLog.WriteAsync("carrier.deleted", actorCustomerId, entityType: "Carrier", entityId: id, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }

    // ---- Tracking ----

    public async Task<CatalogResult<TrackingView>> GetTrackingAsync(TrackingCaller caller, int shopOrderId, CancellationToken cancellationToken)
    {
        var shopOrder = await orders.GetShopOrderAsync(shopOrderId, cancellationToken);
        if (shopOrder is null || !CanSee(caller, shopOrder)) return CatalogResult.Error<TrackingView>(CatalogErrors.NotFound);
        return CatalogResult.Success(await ViewAsync(shopOrder, cancellationToken));
    }

    public async Task<CatalogResult<TrackingView>> AddEventAsync(
        int vendorId, int shopOrderId, AddTrackingEventCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var shopOrder = await orders.GetShopOrderAsync(shopOrderId, cancellationToken);
        // An order that waits for its payment does not exist for the shop yet.
        if (shopOrder is null || shopOrder.VendorId != vendorId || shopOrder.Order?.AwaitingPayment == true)
            return CatalogResult.Error<TrackingView>(CatalogErrors.NotFound);

        var errors = new Dictionary<string, string[]>();
        var status = command.Status?.Trim().ToLowerInvariant() ?? string.Empty;
        var location = Clean(command.Location);
        var note = Clean(command.Note);
        var now = clock.UtcNow;
        var occurred = command.OccurredOnUtc ?? now;

        if (!TrackingRules.Statuses.Contains(status)) errors["status"] = ["Choose a status."];
        if (location?.Length > TrackingLimits.MaxLocationLength) errors["location"] = [$"The location can have at most {TrackingLimits.MaxLocationLength} characters."];
        if (note?.Length > TrackingLimits.MaxNoteLength) errors["note"] = [$"The note can have at most {TrackingLimits.MaxNoteLength} characters."];
        if (occurred > now.AddMinutes(TrackingLimits.FutureToleranceMinutes)) errors["occurredOnUtc"] = ["The time cannot be in the future."];
        else if (occurred < shopOrder.CreatedOnUtc) errors["occurredOnUtc"] = ["The time cannot be before the order was placed."];
        if (errors.Count > 0) return CatalogResult.Failure<TrackingView>(errors);

        // The journey is told while the parcel is on its way.
        if (shopOrder.Status != ShopOrderStatus.Shipped) return CatalogResult.Error<TrackingView>(TrackingErrors.NotShipped);

        var added = await store.TryAddEventAsync(new TrackingEvent
        {
            ShopOrderId = shopOrderId, Status = status, Location = location, Note = note, OccurredOnUtc = occurred,
            CreatedByCustomerId = actorCustomerId, CreatedOnUtc = now
        }, TrackingLimits.MaxEventsPerShopOrder, cancellationToken);
        if (added is null) return CatalogResult.Error<TrackingView>(TrackingErrors.EventLimit);

        // 0 is the system (a carrier's callback): no customer is written as the actor.
        await auditLog.WriteAsync("shipping.tracking_event", actorCustomerId > 0 ? actorCustomerId : null, entityType: "ShopOrder", entityId: shopOrderId,
            details: new { shopOrderId, status }, cancellationToken: cancellationToken);
        return CatalogResult.Success(await ViewAsync(shopOrder, cancellationToken));
    }

    // ---- Helpers ----

    private static bool CanSee(TrackingCaller caller, ShopOrder shopOrder)
    {
        if (caller.IsAdmin) return true;
        if (shopOrder.Order is null) return false;
        if (shopOrder.Order.CustomerId == caller.CustomerId) return true;
        return caller.VendorId == shopOrder.VendorId && !shopOrder.Order.AwaitingPayment;
    }

    private async Task<TrackingView> ViewAsync(ShopOrder shopOrder, CancellationToken cancellationToken)
    {
        string? url = null;
        if (!string.IsNullOrWhiteSpace(shopOrder.Carrier) && !string.IsNullOrWhiteSpace(shopOrder.TrackingNumber))
        {
            var carrier = await store.FindActiveCarrierByNameAsync(shopOrder.Carrier.Trim(), cancellationToken);
            url = TrackingRules.UrlFor(carrier?.TrackingUrlTemplate, shopOrder.TrackingNumber);
        }
        return new TrackingView(
            shopOrder.Id, shopOrder.Carrier, shopOrder.TrackingNumber, url, OrderRules.ToWire(shopOrder.Status), await store.ListEventsAsync(shopOrder.Id, cancellationToken));
    }

    private static Dictionary<string, string[]> Validate(SaveCarrierCommand command, out string name, out string? template)
    {
        var errors = new Dictionary<string, string[]>();
        name = command.Name?.Trim() ?? string.Empty;
        template = Clean(command.TrackingUrlTemplate);
        if (name.Length is 0 or > TrackingLimits.MaxNameLength) errors["name"] = [$"Enter the carrier name (1 to {TrackingLimits.MaxNameLength} characters)."];
        if (template is not null && !TrackingRules.IsValidTemplate(template))
            errors["trackingUrlTemplate"] = [$"Use an http or https address that contains {TrackingRules.Placeholder} (at most {TrackingLimits.MaxTemplateLength} characters)."];
        if (command.DisplayOrder is < 0 or > 10_000) errors["displayOrder"] = ["The order must be between 0 and 10000."];
        return errors;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
