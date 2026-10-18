using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;

namespace Nomori.Marketplace.Core.Shipping;

/// <summary>
/// Giao Hang Nhanh (GHN), the marketplace's carrier (Vietnam only). One GHN shop (the platform's account) sends for every shop, each order
/// carrying the pickup address of its own shop (<see cref="GhnPickup"/>). Addresses use the two-level administrative units in force since
/// 1 July 2025 (province and ward, no district): GHN's "v2" ids.
/// The token and the shop id are secrets: keep them in the environment or the git-ignored .env (<c>Shipping__Ghn__Token</c>, <c>Shipping__Ghn__ShopId</c>).
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

    /// <summary>The weight of a unit whose product has none yet (saved before products carried a weight).</summary>
    public int ItemWeightGrams { get; init; } = 500;

    /// <summary>The box used when no product of the parcel gives its size.</summary>
    public int ParcelLengthCm { get; init; } = 20;
    public int ParcelWidthCm { get; init; } = 15;
    public int ParcelHeightCm { get; init; } = 10;

    /// <summary>What the receiver may do before paying: GHN's codes CHOTHUHANG, CHOXEMHANGKHONGTHU, KHONGCHOXEMHANG.</summary>
    public string RequiredNote { get; init; } = "KHONGCHOXEMHANG";

    /// <summary>
    /// The fee charged when GHN cannot price a parcel (GHN down, or not configured): checkout still works, the platform covers the difference
    /// with GHN's real fee. In VND.
    /// </summary>
    public long FallbackFeeVnd { get; init; } = 30_000;

    /// <summary>Declare the goods' value to GHN, so a lost or damaged parcel is paid back; GHN charges a small insurance fee for it.</summary>
    public bool InsureParcels { get; init; } = true;

    /// <summary>GHN insures at most this value per parcel (VND).</summary>
    public long MaxInsuranceVnd { get; init; } = 5_000_000;

    /// <summary>GHN charges in VND. When no VND currency is set up, a primary unit is worth this many VND.</summary>
    public decimal VndPerPrimaryUnit { get; init; } = 25_000m;

    /// <summary>
    /// GHN does not sign its callbacks. The webhook only answers when this secret is set, and GHN is configured to send it back in the
    /// <c>X-Nomori-Webhook-Secret</c> header (a custom header of the GHN webhook settings). A secret: set it in the environment or .env.
    /// </summary>
    public string WebhookSecret { get; init; } = string.Empty;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Token) && ShopId > 0;
}

// ---- Places (two-level units) ----

public sealed record GhnProvince(int Id, string Name);

public sealed record GhnWard(int Id, int ProvinceId, string Name);

/// <summary>A province and a ward, by GHN's two-level ids.</summary>
public sealed record GhnPlace(int ProvinceId, int WardId);

// ---- Parcels ----

/// <summary>A line of a parcel: which product and how many.</summary>
public sealed record GhnLineRef(int ProductId, int Quantity);

/// <summary>The packed weight and size a product declares; any of them may be missing on older products.</summary>
public sealed record GhnProductSize(int? WeightGrams, int? LengthCm, int? WidthCm, int? HeightCm);

/// <summary>One parcel as GHN prices it: the weight and the box.</summary>
public sealed record GhnParcel(int WeightGrams, int LengthCm, int WidthCm, int HeightCm);

/// <summary>What GHN is asked to price (and to time): from the shop's pickup place to the customer's, one parcel, its declared value.</summary>
public sealed record GhnFeeRequest(GhnPlace From, GhnPlace To, GhnParcel Parcel, long InsuranceVnd, int ServiceTypeId);

/// <summary>When GHN expects to deliver: the earliest and the latest day.</summary>
public sealed record GhnLeadtime(DateTime? FromUtc, DateTime? ToUtc);

/// <summary>Who sends the parcel and where GHN picks it up (the shop). GHN reads the place by the two-level ids and names.</summary>
public sealed record GhnSender(string Name, string Phone, string Address, int ProvinceId, string ProvinceName, int WardId, string WardName);

public sealed record GhnItem(string Name, int Quantity, int WeightGrams, long PriceVnd);

public sealed record GhnCreateOrderRequest(
    string ToName, string ToPhone, string ToAddress, int ToProvinceId, string ToProvinceName, int ToWardId, string ToWardName,
    long CodAmountVnd, long InsuranceVnd, string? Note, string Content, GhnParcel Parcel, int ServiceTypeId, string RequiredNote,
    string ClientOrderCode, IReadOnlyList<GhnItem> Items, GhnSender From);

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
    Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int provinceId, CancellationToken cancellationToken);
    Task<GhnResult<long>> GetFeeAsync(GhnFeeRequest request, CancellationToken cancellationToken);
    Task<GhnResult<GhnLeadtime>> GetLeadtimeAsync(GhnFeeRequest request, CancellationToken cancellationToken);
    Task<GhnResult<GhnCreatedOrder>> CreateOrderAsync(GhnCreateOrderRequest request, CancellationToken cancellationToken);
    Task<GhnResult<GhnOrderDetail>> GetOrderAsync(string orderCode, CancellationToken cancellationToken);

    /// <summary>A page that prints the A5 label of the order.</summary>
    Task<GhnResult<string>> GetLabelUrlAsync(string orderCode, CancellationToken cancellationToken);
}

// ---- Stored data ----

/// <summary>Where an order goes, as GHN needs it (copied from the address when the order was placed).</summary>
public sealed record GhnDestination(
    string RecipientName, string RecipientPhone, string Address1, string? Address2, string City, string? StateProvince, string PaymentMethod,
    int? ProvinceId, string? ProvinceName, int? WardId, string? WardName)
{
    public GhnPlace? Place => ProvinceId is { } province && WardId is { } ward ? new GhnPlace(province, ward) : null;
}

/// <summary>
/// Where GHN picks up one shop's parcels. A shop has GHN only once it has one: the parcels leave from the shop itself, not from the platform.
/// </summary>
public sealed class GhnPickup
{
    public int VendorId { get; set; }
    public string ContactName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int ProvinceId { get; set; }
    public string ProvinceName { get; set; } = string.Empty;
    public int WardId { get; set; }
    public string WardName { get; set; } = string.Empty;

    /// <summary>The shop pays the shipping of its orders from this subtotal (primary currency): the customer sees it free. Null: no free shipping.</summary>
    public decimal? FreeShippingOverSubtotal { get; set; }

    public DateTime UpdatedOnUtc { get; set; }

    public GhnPlace Place => new(ProvinceId, WardId);

    public GhnSender ToSender() => new(ContactName, Phone, Address, ProvinceId, ProvinceName, WardId, WardName);
}

public sealed record SaveGhnPickupCommand(
    string? ContactName, string? Phone, string? Address, int ProvinceId, string? ProvinceName, int WardId, string? WardName,
    decimal? FreeShippingOverSubtotal = null);

/// <summary>The shop order GHN carries under a GHN order code, with what was declared and paid when it was handed over.</summary>
public sealed record GhnShopOrderRef(
    int ShopOrderId, int VendorId, int OrderId, decimal Subtotal, decimal DiscountAmount, int? ParcelWeightGrams, decimal? CarrierFee);

/// <summary>What is recorded when GHN takes a parcel: its real fee, the weight it was declared at, the cash to collect, the expected day.</summary>
public sealed record GhnShipment(int ShopOrderId, decimal CarrierFee, int ParcelWeightGrams, decimal CodAmount, DateTime? ExpectedDeliveryUtc);

/// <summary>What a GHN webhook says (only the fields used here). GHN sends PascalCase JSON; every field is present.</summary>
public sealed record GhnWebhook(
    int ShopID, string? Type, string? OrderCode, string? Status, string? Time, string? Description, string? Reason,
    int Weight = 0, int ConvertedWeight = 0, long TotalFee = 0, string? CODTransferDate = null);

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

    /// <summary>The packed weight and size of these products (product id to size); unknown ids are left out.</summary>
    Task<IReadOnlyDictionary<int, GhnProductSize>> GetProductSizesAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken);

    Task SaveShipmentAsync(GhnShipment shipment, CancellationToken cancellationToken);

    /// <summary>What the shop owes for a parcel GHN weighed heavier than declared (replaces an earlier figure, never below 0).</summary>
    Task SetShippingAdjustmentAsync(int shopOrderId, decimal amount, CancellationToken cancellationToken);

    /// <summary>GHN paid the cash it collected for this parcel to the platform. Only the first date is kept.</summary>
    Task MarkCodTransferredAsync(int shopOrderId, DateTime transferredOnUtc, CancellationToken cancellationToken);
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
    /// <summary>The shipping option of GHN at checkout (a fixed id: shops no longer have rates of their own).</summary>
    public const int RateId = -100;

    /// <summary>What the customer sees: the carrier is the platform's business, so its name is not shown to customers.</summary>
    public const string MethodName = "Giao hàng tiêu chuẩn";

    /// <summary>The option when GHN could not price the parcel and the fallback fee is charged.</summary>
    public const string FallbackMethodName = "Giao hàng tiêu chuẩn (phí tạm tính)";

    /// <summary>A shop order GHN carries: the customer paid its shipping to the platform, which pays GHN, so the shop is not owed it.</summary>
    public static bool IsPlatformShipped(int? shippingRateId) => shippingRateId == RateId;

    /// <summary>
    /// The option for a shop: free for the customer when the shop's threshold is reached (the shop takes the fee on), the fee otherwise;
    /// with GHN's expected delivery days when it gave them.
    /// </summary>
    public static ShippingOption Option(decimal fee, bool fallback, decimal subtotal, decimal? freeOver, GhnLeadtime? leadtime = null)
    {
        var name = fallback ? FallbackMethodName : MethodName;
        var option = freeOver is { } threshold && subtotal >= threshold
            ? new ShippingOption(RateId, name, 0m, true, null, null, fee)
            : new ShippingOption(RateId, name, fee, fee == 0m, null, null);
        return option with { EstimatedFromUtc = leadtime?.FromUtc, EstimatedToUtc = leadtime?.ToUtc };
    }

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

    /// <summary>The note of a tracking line that came from GHN (customers read it, so it does not name the carrier); also how a repeat is recognised.</summary>
    public static string NoteOf(string ghnStatus) => LabelOf(ghnStatus);

    /// <summary>A Vietnamese number as GHN accepts it: 0 and 9 or 10 more digits, or the same after +84 / 84.</summary>
    public static bool IsVietnamesePhone(string phone)
    {
        var local = phone.StartsWith("+84", StringComparison.Ordinal) ? "0" + phone[3..] : phone.StartsWith("84", StringComparison.Ordinal) && phone.Length >= 11 ? "0" + phone[2..] : phone;
        return local.Length is 10 or 11 && local[0] == '0' && local.All(char.IsAsciiDigit);
    }

    private const long MaxStackCm = 200;

    /// <summary>
    /// The parcel of some lines. Its weight is the sum of every unit's packed weight (a product with none counts as
    /// <paramref name="settings"/>' unit weight). Its box stacks the units that give a size: the longest length, the widest width and the
    /// heights added up (at most 200 cm, GHN's limit); with no size at all it is the default box. GHN then charges by the heavier of the
    /// weight and the box's volumetric weight.
    /// </summary>
    public static GhnParcel Parcel(IEnumerable<(int Quantity, GhnProductSize? Size)> lines, GhnOptions settings)
    {
        var list = lines.Where(l => l.Quantity > 0).ToList();
        var weight = list.Sum(l => (long)l.Quantity * (l.Size?.WeightGrams ?? Math.Max(1, settings.ItemWeightGrams)));
        var sized = list.Where(l => l.Size is { LengthCm: not null, WidthCm: not null, HeightCm: not null }).ToList();
        var (length, width, height) = sized.Count == 0
            ? (settings.ParcelLengthCm, settings.ParcelWidthCm, settings.ParcelHeightCm)
            : (sized.Max(l => l.Size!.LengthCm!.Value), sized.Max(l => l.Size!.WidthCm!.Value),
               (int)Math.Min(MaxStackCm, sized.Sum(l => (long)l.Quantity * l.Size!.HeightCm!.Value)));
        return new GhnParcel((int)Math.Clamp(weight, 1, int.MaxValue), length, width, height);
    }

    /// <summary>What GHN charges by: the heavier of the weight and the box's volumetric weight (cm³ ÷ 5, in grams).</summary>
    public static int ChargeableWeight(GhnParcel parcel) =>
        Math.Max(parcel.WeightGrams, (int)Math.Min(int.MaxValue, (long)parcel.LengthCm * parcel.WidthCm * parcel.HeightCm / 5));

    /// <summary>The value declared to GHN: the goods, up to GHN's ceiling; nothing when parcels are not insured.</summary>
    public static long InsuranceOf(long goodsVnd, GhnOptions settings) =>
        settings.InsureParcels ? Math.Clamp(goodsVnd, 0, Math.Max(0, settings.MaxInsuranceVnd)) : 0;

    /// <summary>
    /// What the shop owes when GHN weighed the parcel heavier than it was declared: the rise in GHN's fee. Nothing when GHN found it no
    /// heavier (a fee that changed for another reason is the platform's business) or when the new fee is not higher.
    /// </summary>
    public static decimal WeightAdjustment(int declaredGrams, int measuredGrams, decimal declaredFee, decimal measuredFee) =>
        measuredGrams > declaredGrams && measuredFee > declaredFee ? measuredFee - declaredFee : 0m;
}

public interface IGhnShippingService
{
    bool IsConfigured { get; }

    Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken);
    Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int provinceId, CancellationToken cancellationToken);

    /// <summary>
    /// The GHN option for the lines of one shop going to a province and ward, priced from the shop's pickup address, in the primary
    /// currency, with GHN's expected delivery days, free for the customer over the shop's threshold. When GHN cannot price it (down, or not
    /// configured) the fallback fee is used. Null when GHN is configured and the shop has no pickup address, or the address has no GHN place.
    /// </summary>
    Task<ShippingOption?> QuoteAsync(int vendorId, GhnPlace? destination, IReadOnlyList<GhnLineRef> lines, decimal subtotal, CancellationToken cancellationToken);

    /// <summary>Whether the shop can sell: GHN is off (nothing to check), or the shop has a pickup address.</summary>
    Task<bool> CanShipAsync(int vendorId, CancellationToken cancellationToken);

    // ---- The shop's pickup address (shop members) ----

    Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken);
    Task<CatalogResult<GhnPickup>> SavePickupAsync(int vendorId, SaveGhnPickupCommand command, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Stops offering GHN for the shop. Orders already handed to GHN keep going.</summary>
    Task DeletePickupAsync(int vendorId, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>
    /// Asks GHN to pick up a confirmed shop order, records GHN's real fee, the declared weight, the cash to collect and the expected day, then
    /// marks it shipped with the GHN order code as its tracking number.
    /// </summary>
    Task<CatalogResult<ShopOrderDetail>> CreateShipmentAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Reads the GHN journey into tracking lines; GHN's "delivered" marks the shop order delivered.</summary>
    Task<CatalogResult<ShopOrderDetail>> SyncAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken);

    Task<CatalogResult<string>> GetLabelUrlAsync(int vendorId, int shopOrderId, CancellationToken cancellationToken);

    /// <summary>Whether a callback carries the configured webhook secret. False when no secret is set: the webhook is off.</summary>
    bool IsWebhookSecret(string? secret);

    /// <summary>
    /// A GHN callback: a status change becomes a tracking line (and "delivered" marks the shop order delivered), a heavier weight makes the
    /// shop owe the rise in fee, and a COD transfer is recorded. Repeats change nothing.
    /// </summary>
    Task<GhnWebhookOutcome> HandleWebhookAsync(GhnWebhook webhook, CancellationToken cancellationToken);
}
