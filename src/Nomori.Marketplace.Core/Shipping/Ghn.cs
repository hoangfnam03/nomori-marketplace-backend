using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;

namespace Nomori.Marketplace.Core.Shipping;

/// <summary>
/// Giao Hang Nhanh (GHN), a Vietnamese carrier, as a live shipping option. Prototype: one GHN shop (the platform's account) sends for every
/// shop of the marketplace, each order carrying the pickup address of its own shop (<see cref="GhnPickup"/>); products have no weight yet
/// (every unit counts as <see cref="ItemWeightGrams"/>).
/// The token and the shop id are secrets: set them as environment variables (<c>Shipping__Ghn__Token</c>, <c>Shipping__Ghn__ShopId</c>).
/// </summary>
public sealed class GhnOptions
{
    public const string SectionName = "Shipping:Ghn";

    public bool Enabled { get; init; }

    /// <summary>The test gateway by default; production is <c>https://online-gateway.ghn.vn/shiip/public-api</c>.</summary>
    public string BaseUrl { get; init; } = "https://dev-online-gateway.ghn.vn/shiip/public-api";

    public string Token { get; init; } = string.Empty;
    public int ShopId { get; init; }

    /// <summary>2 is GHN's standard (e-commerce) service.</summary>
    public int ServiceTypeId { get; init; } = 2;

    public int ItemWeightGrams { get; init; } = 500;
    public int ParcelLengthCm { get; init; } = 20;
    public int ParcelWidthCm { get; init; } = 15;
    public int ParcelHeightCm { get; init; } = 10;

    /// <summary>What the receiver may do before paying: GHN's codes CHOTHUHANG, CHOXEMHANGKHONGTHU, KHONGCHOXEMHANG.</summary>
    public string RequiredNote { get; init; } = "KHONGCHOXEMHANG";

    /// <summary>GHN charges in VND. When no VND currency is set up, a primary unit is worth this many VND.</summary>
    public decimal VndPerPrimaryUnit { get; init; } = 25_000m;

    /// <summary>
    /// GHN does not sign its callbacks. The webhook only answers when this secret is set, and GHN is configured to send it back in the
    /// <c>X-Nomori-Webhook-Secret</c> header (a custom header of the GHN webhook settings). A secret: set it as an environment variable.
    /// </summary>
    public string WebhookSecret { get; init; } = string.Empty;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Token) && ShopId > 0;
}

public sealed record GhnProvince(int Id, string Name);

public sealed record GhnDistrict(int Id, int ProvinceId, string Name);

public sealed record GhnWard(string Code, int DistrictId, string Name);

/// <param name="FromDistrictId">The shop's pickup district; GHN prices from there instead of the GHN shop's own address.</param>
public sealed record GhnFeeRequest(
    int ToDistrictId, string ToWardCode, int WeightGrams, int LengthCm, int WidthCm, int HeightCm, int ServiceTypeId, int FromDistrictId, string FromWardCode);

/// <summary>Who sends the parcel and where GHN picks it up. GHN reads the place by names (ward, district, province).</summary>
public sealed record GhnSender(string Name, string Phone, string Address, string WardName, string DistrictName, string ProvinceName);

public sealed record GhnItem(string Name, int Quantity, int WeightGrams, long PriceVnd);

public sealed record GhnCreateOrderRequest(
    string ToName, string ToPhone, string ToAddress, int ToDistrictId, string ToWardCode, long CodAmountVnd, string? Note, string Content,
    int WeightGrams, int LengthCm, int WidthCm, int HeightCm, int ServiceTypeId, string RequiredNote, string ClientOrderCode,
    IReadOnlyList<GhnItem> Items, GhnSender From);

public sealed record GhnCreatedOrder(string OrderCode, long TotalFeeVnd, DateTime? ExpectedDeliveryUtc);

public sealed record GhnLog(string Status, DateTime UpdatedUtc);

public sealed record GhnOrderDetail(string OrderCode, string Status, IReadOnlyList<GhnLog> Logs);

/// <summary>A GHN answer: the value, or GHN's own message when it refused.</summary>
public sealed record GhnResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;
}

public static class GhnResult
{
    public static GhnResult<T> Ok<T>(T value) => new(value, null);
    public static GhnResult<T> Fail<T>(string error) => new(default, error);
}

/// <summary>The GHN public API. Every call answers a result instead of throwing, so a carrier outage never breaks checkout.</summary>
public interface IGhnClient
{
    Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken);
    Task<GhnResult<IReadOnlyList<GhnDistrict>>> GetDistrictsAsync(int provinceId, CancellationToken cancellationToken);
    Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int districtId, CancellationToken cancellationToken);
    Task<GhnResult<long>> GetFeeAsync(GhnFeeRequest request, CancellationToken cancellationToken);
    Task<GhnResult<GhnCreatedOrder>> CreateOrderAsync(GhnCreateOrderRequest request, CancellationToken cancellationToken);
    Task<GhnResult<GhnOrderDetail>> GetOrderAsync(string orderCode, CancellationToken cancellationToken);

    /// <summary>A page that prints the A5 label of the order.</summary>
    Task<GhnResult<string>> GetLabelUrlAsync(string orderCode, CancellationToken cancellationToken);
}

/// <summary>Where an order goes, as GHN needs it (copied from the address when the order was placed).</summary>
public sealed record GhnDestination(
    string RecipientName, string RecipientPhone, string Address1, string? Address2, string City, string? StateProvince, string PaymentMethod,
    int? DistrictId, string? WardCode, string? Area);

/// <summary>
/// Where GHN picks up one shop's parcels. A shop has GHN only once it has one: the parcels leave from the shop itself, not from the platform.
/// GHN's own ids price the parcel; the names (and the street) go on the shipping order.
/// </summary>
public sealed class GhnPickup
{
    public int VendorId { get; set; }
    public string ContactName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int ProvinceId { get; set; }
    public string ProvinceName { get; set; } = string.Empty;
    public int DistrictId { get; set; }
    public string DistrictName { get; set; } = string.Empty;
    public string WardCode { get; set; } = string.Empty;
    public string WardName { get; set; } = string.Empty;
    public DateTime UpdatedOnUtc { get; set; }

    public GhnSender ToSender() => new(ContactName, Phone, Address, WardName, DistrictName, ProvinceName);
}

public sealed record SaveGhnPickupCommand(
    string? ContactName, string? Phone, string? Address, int ProvinceId, string? ProvinceName, int DistrictId, string? DistrictName, string? WardCode,
    string? WardName);

/// <summary>The shop order GHN carries under a GHN order code.</summary>
public sealed record GhnShopOrderRef(int ShopOrderId, int VendorId);

/// <summary>What a GHN webhook says (only the fields used here). GHN sends PascalCase JSON; every field is present.</summary>
public sealed record GhnWebhook(int ShopID, string? Type, string? OrderCode, string? Status, string? Time, string? Description, string? Reason);

public enum GhnWebhookOutcome
{
    /// <summary>Acted on, or nothing to do (another event type, a repeat): answer 200 so GHN does not retry.</summary>
    Handled = 0,
    /// <summary>Not for this platform: an unknown order or another GHN shop. Answer 200 too; a retry would not change anything.</summary>
    Ignored = 1
}

public interface IGhnStore
{
    Task<GhnDestination?> GetDestinationAsync(int orderId, CancellationToken cancellationToken);
    Task<GhnShopOrderRef?> FindByOrderCodeAsync(string orderCode, CancellationToken cancellationToken);

    Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken);
    Task SavePickupAsync(GhnPickup pickup, CancellationToken cancellationToken);
    Task<bool> DeletePickupAsync(int vendorId, CancellationToken cancellationToken);
}

/// <summary>Business-rule codes of the GHN integration (HTTP 409). A refusal from GHN itself is a field error on <c>ghn</c>.</summary>
public static class GhnErrors
{
    public const string NotConfigured = "ghn.not_configured";
    public const string NotConfirmed = "ghn.not_confirmed";
    public const string NoArea = "ghn.no_area";
    public const string NotGhn = "ghn.not_ghn";
    public const string NoPickup = "ghn.no_pickup";
}

public static class GhnRules
{
    /// <summary>The shipping option of GHN at checkout. Shop rates have positive ids, so this one never meets a rate.</summary>
    public const int RateId = -100;

    public const string MethodName = "Giao Hàng Nhanh (GHN)";

    /// <summary>The name written on the shop order when GHN takes the parcel; matches the carrier seeded by F20-B.</summary>
    public const string CarrierName = "Giao Hang Nhanh";

    public static bool IsGhnOrder(ShopOrder shopOrder) =>
        string.Equals(shopOrder.Carrier, CarrierName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(shopOrder.TrackingNumber);

    /// <summary>The F20-B tracking status for a GHN status; null for GHN's "delivered", which also moves the shop order (its line is info).</summary>
    public static string? TrackingStatusOf(string ghnStatus) => ghnStatus switch
    {
        "delivered" => null,
        "picked" => TrackingRules.PickedUp,
        "storing" or "transporting" or "sorting" => TrackingRules.InTransit,
        "delivering" or "money_collect_delivering" => TrackingRules.OutForDelivery,
        "delivery_fail" => TrackingRules.DeliveryAttempted,
        "cancel" or "exception" or "damage" or "lost" or "return" or "returning" or "return_transporting" or "return_sorting"
            or "return_fail" or "returned" or "waiting_to_return" => TrackingRules.Exception,
        _ => TrackingRules.Info
    };

    /// <summary>GHN's status in words, for the note of a tracking line.</summary>
    public static string LabelOf(string ghnStatus) => ghnStatus switch
    {
        "ready_to_pick" => "Chờ lấy hàng",
        "picking" => "Đang lấy hàng",
        "money_collect_picking" => "Đang thu tiền người gửi",
        "picked" => "Đã lấy hàng",
        "storing" => "Hàng đang nằm ở kho",
        "transporting" => "Đang luân chuyển",
        "sorting" => "Đang phân loại",
        "delivering" => "Đang giao hàng",
        "money_collect_delivering" => "Đang thu tiền người nhận",
        "delivered" => "Giao hàng thành công",
        "delivery_fail" => "Giao hàng thất bại",
        "waiting_to_return" => "Chờ trả hàng",
        "return" or "returning" or "return_transporting" or "return_sorting" => "Đang trả hàng",
        "return_fail" => "Trả hàng thất bại",
        "returned" => "Đã trả hàng",
        "cancel" => "Đã huỷ",
        "exception" => "Đơn hàng ngoại lệ",
        "damage" => "Hàng bị hư hỏng",
        "lost" => "Hàng bị thất lạc",
        _ => ghnStatus
    };

    /// <summary>A Vietnamese number as GHN accepts it: 0 and 9 or 10 more digits, or the same after +84 / 84.</summary>
    public static bool IsVietnamesePhone(string phone)
    {
        var local = phone.StartsWith("+84", StringComparison.Ordinal) ? "0" + phone[3..] : phone.StartsWith("84", StringComparison.Ordinal) && phone.Length >= 11 ? "0" + phone[2..] : phone;
        return local.Length is 10 or 11 && local[0] == '0' && local.All(char.IsAsciiDigit);
    }

    /// <summary>Every unit weighs the same until products carry a weight; GHN wants at least 1 gram.</summary>
    public static int WeightOf(IEnumerable<int> quantities, int gramsPerUnit) => Math.Max(1, quantities.Sum() * Math.Max(1, gramsPerUnit));

    /// <summary>The note of a tracking line that came from GHN, so a second sync does not add it again.</summary>
    public static string NoteOf(string ghnStatus) => $"GHN: {LabelOf(ghnStatus)}";
}

public interface IGhnShippingService
{
    bool IsConfigured { get; }

    Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken);
    Task<GhnResult<IReadOnlyList<GhnDistrict>>> GetDistrictsAsync(int provinceId, CancellationToken cancellationToken);
    Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int districtId, CancellationToken cancellationToken);

    /// <summary>
    /// The GHN option for the lines of one shop going to a district and ward, priced from the shop's pickup address, in the primary currency.
    /// Null when the shop has no pickup address or GHN cannot quote.
    /// </summary>
    Task<ShippingOption?> QuoteAsync(int vendorId, int districtId, string wardCode, IReadOnlyList<int> quantities, CancellationToken cancellationToken);

    // ---- The shop's pickup address (shop members) ----

    Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken);
    Task<CatalogResult<GhnPickup>> SavePickupAsync(int vendorId, SaveGhnPickupCommand command, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Stops offering GHN for the shop. Orders already handed to GHN keep going.</summary>
    Task DeletePickupAsync(int vendorId, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Asks GHN to pick up a confirmed shop order, then marks it shipped with the GHN order code as its tracking number.</summary>
    Task<CatalogResult<ShopOrderDetail>> CreateShipmentAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Reads the GHN journey into tracking lines; GHN's "delivered" marks the shop order delivered.</summary>
    Task<CatalogResult<ShopOrderDetail>> SyncAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken);

    Task<CatalogResult<string>> GetLabelUrlAsync(int vendorId, int shopOrderId, CancellationToken cancellationToken);

    /// <summary>Whether a callback carries the configured webhook secret. False when no secret is set: the webhook is off.</summary>
    bool IsWebhookSecret(string? secret);

    /// <summary>A GHN status change: a tracking line, and GHN's "delivered" marks the shop order delivered. Repeats change nothing.</summary>
    Task<GhnWebhookOutcome> HandleWebhookAsync(GhnWebhook webhook, CancellationToken cancellationToken);
}
