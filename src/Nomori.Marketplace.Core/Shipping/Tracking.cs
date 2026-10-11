using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Shipping;

public static class TrackingLimits
{
    public const int MaxCarriers = 50;
    public const int MaxEventsPerShopOrder = 50;
    public const int MaxNameLength = 100;
    public const int MaxTemplateLength = 500;
    public const int MaxLocationLength = 100;
    public const int MaxNoteLength = 500;

    /// <summary>How far ahead of the clock a shop may date an event (clocks differ a little).</summary>
    public const int FutureToleranceMinutes = 5;
}

/// <summary>Business-rule codes of carriers and tracking (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class TrackingErrors
{
    public const string CarrierNameExists = "carrier.name_exists";
    public const string CarrierLimit = "carrier.limit";
    public const string NotShipped = "tracking.not_shipped";
    public const string EventLimit = "tracking.event_limit";
}

/// <summary>
/// A carrier shops can pick when they ship. The shop order keeps the carrier's name as text (F18-A); the template, when the name matches an
/// active carrier, gives the customer a link to the carrier's own tracking page.
/// </summary>
public sealed class Carrier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>An http(s) address with <c>{tracking}</c> where the tracking number goes; null when the carrier has no public page.</summary>
    public string? TrackingUrlTemplate { get; set; }

    public bool Active { get; set; } = true;
    public int DisplayOrder { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public sealed record SaveCarrierCommand(string? Name, string? TrackingUrlTemplate, bool Active, int DisplayOrder);

/// <summary>One line of a parcel's journey. Append-only: a wrong line is answered by a newer one.</summary>
public sealed class TrackingEvent
{
    public long Id { get; set; }
    public int ShopOrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Location { get; set; }
    public string? Note { get; set; }
    public DateTime OccurredOnUtc { get; set; }
    public int CreatedByCustomerId { get; set; }
    public DateTime CreatedOnUtc { get; set; }
}

public sealed record AddTrackingEventCommand(string? Status, string? Location, string? Note, DateTime? OccurredOnUtc);

/// <summary>What the customer, the shop and the administrator all see about one parcel.</summary>
public sealed record TrackingView(
    int ShopOrderId, string? Carrier, string? TrackingNumber, string? TrackingUrl, string OrderStatus, IReadOnlyList<TrackingEvent> Events);

/// <param name="IsAdmin">Allowed to manage orders.</param>
public sealed record TrackingCaller(bool IsAdmin, int CustomerId, int? VendorId);

public static class TrackingRules
{
    public const string PickedUp = "picked_up";
    public const string InTransit = "in_transit";
    public const string OutForDelivery = "out_for_delivery";
    public const string DeliveryAttempted = "delivery_attempted";
    public const string Exception = "exception";
    public const string Info = "info";

    public static readonly IReadOnlyList<string> Statuses = [PickedUp, InTransit, OutForDelivery, DeliveryAttempted, Exception, Info];

    public const string Placeholder = "{tracking}";

    /// <summary>Only a web address with the placeholder is a template: anything else could send a customer somewhere unexpected.</summary>
    public static bool IsValidTemplate(string? template) =>
        template is { Length: > 0 and <= TrackingLimits.MaxTemplateLength }
        && template.Contains(Placeholder, StringComparison.Ordinal)
        && Uri.TryCreate(template.Replace(Placeholder, "x", StringComparison.Ordinal), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    public static string? UrlFor(string? template, string? trackingNumber) =>
        string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(trackingNumber)
            ? null
            : template.Replace(Placeholder, Uri.EscapeDataString(trackingNumber.Trim()), StringComparison.Ordinal);
}

public interface ITrackingStore
{
    Task<IReadOnlyList<Carrier>> ListCarriersAsync(bool activeOnly, CancellationToken cancellationToken);
    Task<Carrier?> GetCarrierAsync(int id, CancellationToken cancellationToken);
    Task<Carrier?> FindActiveCarrierByNameAsync(string name, CancellationToken cancellationToken);
    Task<int> CountCarriersAsync(CancellationToken cancellationToken);
    Task<bool> CarrierNameExistsAsync(string name, int excludeId, CancellationToken cancellationToken);
    Task<int> InsertCarrierAsync(Carrier carrier, CancellationToken cancellationToken);
    Task UpdateCarrierAsync(Carrier carrier, CancellationToken cancellationToken);
    Task<bool> DeleteCarrierAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the event unless the shop order already has the maximum; counted and written in one statement so two requests cannot pass the limit together.
    /// Null when the limit was reached.
    /// </summary>
    Task<long?> TryAddEventAsync(TrackingEvent trackingEvent, int maxEvents, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrackingEvent>> ListEventsAsync(int shopOrderId, CancellationToken cancellationToken);
}

public interface ITrackingService
{
    Task<IReadOnlyList<Carrier>> GetCarriersAsync(bool activeOnly, CancellationToken cancellationToken);
    Task<CatalogResult<Carrier>> CreateCarrierAsync(SaveCarrierCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<Carrier>> UpdateCarrierAsync(int id, SaveCarrierCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteCarrierAsync(int id, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>The parcel of a shop order for the customer who bought it, a member of the shop, or an administrator. Anyone else: not found.</summary>
    Task<CatalogResult<TrackingView>> GetTrackingAsync(TrackingCaller caller, int shopOrderId, CancellationToken cancellationToken);

    Task<CatalogResult<TrackingView>> AddEventAsync(int vendorId, int shopOrderId, AddTrackingEventCommand command, int actorCustomerId, CancellationToken cancellationToken);
}
