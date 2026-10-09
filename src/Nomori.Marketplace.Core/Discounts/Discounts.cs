using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Discounts;

public static class DiscountLimits
{
    public const int MaxNameLength = 100;
    public const int MinCodeLength = 3;
    public const int MaxCodeLength = 32;
    public const int MaxUses = 1_000_000;
    public const decimal MaxAmount = 1_000_000_000m;
    public const int MaxPerShop = 200;
    public const int MaxPlatform = 500;

    /// <summary>At most this many codes are offered to pick from at checkout.</summary>
    public const int MaxOffers = 50;
}

/// <summary>Business-rule codes of discounts (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class DiscountErrors
{
    public const string CodeExists = "discount.code_exists";
    public const string Limit = "discount.limit";
    public const string InUse = "discount.in_use";
}

/// <summary>Why a code does not apply to a cart. Stable codes the screen translates.</summary>
public static class DiscountReasons
{
    public const string NotFound = "not_found";
    public const string Disabled = "disabled";
    public const string NotStarted = "not_started";
    public const string Expired = "expired";
    public const string MinSubtotal = "min_subtotal";
    public const string LimitReached = "limit_reached";
    public const string CustomerLimitReached = "customer_limit_reached";
    public const string NothingToDiscount = "nothing_to_discount";
}

/// <summary>The stored value is part of the database contract: add at the end, never renumber.</summary>
public enum DiscountType
{
    Percentage = 0,
    Fixed = 1
}

/// <summary>Who bears the cost. A platform discount covers the whole cart, a shop discount only that shop's lines.</summary>
public enum DiscountFunding
{
    Platform = 0,
    Shop = 1
}

public sealed class Discount
{
    public int Id { get; set; }

    /// <summary>The shop that funds and owns the discount; null for a platform discount.</summary>
    public int? VendorId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Upper case, unique across the platform.</summary>
    public string Code { get; set; } = string.Empty;

    public DiscountType Type { get; set; }

    /// <summary>A percentage (0 to 100) or an amount in the primary currency, by <see cref="Type"/>.</summary>
    public decimal Value { get; set; }

    /// <summary>Caps a percentage discount.</summary>
    public decimal? MaxDiscountAmount { get; set; }

    public DateTime? StartsOnUtc { get; set; }
    public DateTime? EndsOnUtc { get; set; }

    /// <summary>The eligible subtotal has to reach this amount.</summary>
    public decimal? MinSubtotal { get; set; }

    public int? MaxUses { get; set; }
    public int? MaxUsesPerCustomer { get; set; }
    public int UsedCount { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }

    public DiscountFunding Funding => VendorId is null ? DiscountFunding.Platform : DiscountFunding.Shop;
}

public sealed record SaveDiscountCommand(
    string? Name, string? Code, string? Type, decimal Value, decimal? MaxDiscountAmount, DateTime? StartsOnUtc, DateTime? EndsOnUtc,
    decimal? MinSubtotal, int? MaxUses, int? MaxUsesPerCustomer, bool Enabled);

/// <summary>What a code does to a cart: the amount, and how it is split across the shops of the cart.</summary>
public sealed record AppliedDiscount(Discount Discount, decimal Amount, IReadOnlyDictionary<int, decimal> Split);

/// <summary>Either the discount a code gives, or the reason it gives none.</summary>
public sealed record CouponCheck(AppliedDiscount? Applied, string? Reason);

/// <summary>
/// A code offered at checkout: the amount it takes off this cart, or the reason it cannot be used (then the screen shows it disabled).
/// <paramref name="Shortfall"/> is how much more the customer has to buy when the reason is the minimum subtotal.
/// </summary>
public sealed record CouponOffer(Discount Discount, decimal? Amount, string? Reason, decimal? Shortfall);

public enum RedeemOutcome
{
    Redeemed = 0,
    Unavailable = 1,
    LimitReached = 2,
    CustomerLimitReached = 3
}

public sealed record RedeemRequest(int DiscountId, int CustomerId, int OrderId, decimal Amount, DateTime NowUtc);

public static class DiscountRules
{
    /// <summary>Trimmed and upper case, so "welcome10" and "WELCOME10" are one code.</summary>
    public static string NormalizeCode(string? code) => code?.Trim().ToUpperInvariant() ?? string.Empty;

    public static bool IsValidCode(string code) =>
        code.Length is >= DiscountLimits.MinCodeLength and <= DiscountLimits.MaxCodeLength
        && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    public static string ToWire(DiscountType type) => type == DiscountType.Percentage ? "percentage" : "fixed";

    public static bool TryParseType(string? value, out DiscountType type)
    {
        type = default;
        if (string.Equals(value, "percentage", StringComparison.OrdinalIgnoreCase)) type = DiscountType.Percentage;
        else if (string.Equals(value, "fixed", StringComparison.OrdinalIgnoreCase)) type = DiscountType.Fixed;
        else return false;
        return true;
    }

    public static string ToWire(DiscountFunding funding) => funding == DiscountFunding.Platform ? "platform" : "shop";

    /// <summary>
    /// What a code gives for the subtotals of the shops in the cart (vendor id to subtotal). The reasons are checked in a fixed order.
    /// A shop discount only looks at its own shop; a platform discount at the whole cart.
    /// </summary>
    public static CouponCheck Evaluate(Discount discount, int customerUses, DateTime nowUtc, IReadOnlyDictionary<int, decimal> shopSubtotals, int decimalPlaces)
    {
        // A shop discount whose shop is not in the cart does not exist for this customer.
        if (discount.VendorId is { } vendorId && !shopSubtotals.ContainsKey(vendorId)) return Refuse(DiscountReasons.NotFound);

        var eligible = discount.VendorId is { } shop ? shopSubtotals[shop] : shopSubtotals.Values.Sum();

        if (!discount.Enabled) return Refuse(DiscountReasons.Disabled);
        if (discount.StartsOnUtc is { } start && nowUtc < start) return Refuse(DiscountReasons.NotStarted);
        if (discount.EndsOnUtc is { } end && nowUtc >= end) return Refuse(DiscountReasons.Expired);
        if (discount.MinSubtotal is { } minimum && eligible < minimum) return Refuse(DiscountReasons.MinSubtotal);
        if (discount.MaxUses is { } maxUses && discount.UsedCount >= maxUses) return Refuse(DiscountReasons.LimitReached);
        if (discount.MaxUsesPerCustomer is { } perCustomer && customerUses >= perCustomer) return Refuse(DiscountReasons.CustomerLimitReached);

        var amount = AmountFor(discount, eligible, decimalPlaces);
        if (amount <= 0m) return Refuse(DiscountReasons.NothingToDiscount);

        var split = discount.VendorId is { } owner
            ? new Dictionary<int, decimal> { [owner] = amount }
            : Split(amount, shopSubtotals, decimalPlaces);
        return new CouponCheck(new AppliedDiscount(discount, amount, split), null);
    }

    private static CouponCheck Refuse(string reason) => new(null, reason);

    /// <summary>
    /// Whether a code is offered for a cart: switched on, not over, and the platform's or a shop's of the cart. A code that has not started yet
    /// is offered (disabled, with its start), so the customer knows it is coming.
    /// </summary>
    public static bool IsOffered(Discount discount, DateTime nowUtc, IReadOnlyCollection<int> vendorIds) =>
        discount.Enabled
        && (discount.EndsOnUtc is not { } end || nowUtc < end)
        && (discount.VendorId is not { } vendorId || vendorIds.Contains(vendorId));

    /// <summary>The offer of one code for the subtotals of the shops in the cart, with the same checks as typing the code.</summary>
    public static CouponOffer Offer(Discount discount, int customerUses, DateTime nowUtc, IReadOnlyDictionary<int, decimal> shopSubtotals, int decimalPlaces)
    {
        var check = Evaluate(discount, customerUses, nowUtc, shopSubtotals, decimalPlaces);
        if (check.Applied is { } applied) return new CouponOffer(discount, applied.Amount, null, null);

        decimal? shortfall = null;
        if (check.Reason == DiscountReasons.MinSubtotal && discount.MinSubtotal is { } minimum)
        {
            var eligible = discount.VendorId is { } shop ? shopSubtotals.GetValueOrDefault(shop) : shopSubtotals.Values.Sum();
            shortfall = minimum - eligible;
        }
        return new CouponOffer(discount, null, check.Reason, shortfall);
    }

    /// <summary>Usable codes first, the biggest discount first; then the others, the closest to being usable (smallest shortfall) first.</summary>
    public static IReadOnlyList<CouponOffer> Rank(IEnumerable<CouponOffer> offers) =>
        offers.OrderBy(o => o.Amount is null)
            .ThenByDescending(o => o.Amount ?? 0m)
            .ThenBy(o => o.Shortfall ?? decimal.MaxValue)
            .ThenBy(o => o.Discount.Code, StringComparer.Ordinal)
            .ToList();

    /// <summary>The amount for an eligible subtotal: a percentage (capped) or a fixed amount, never above the subtotal.</summary>
    public static decimal AmountFor(Discount discount, decimal eligibleSubtotal, int decimalPlaces)
    {
        var amount = discount.Type == DiscountType.Percentage
            ? Math.Round(eligibleSubtotal * discount.Value / 100m, decimalPlaces, MidpointRounding.AwayFromZero)
            : discount.Value;
        if (discount.Type == DiscountType.Percentage && discount.MaxDiscountAmount is { } cap) amount = Math.Min(amount, cap);
        return Math.Min(amount, eligibleSubtotal);
    }

    /// <summary>
    /// Splits an amount over the shops in proportion to their subtotals. Each part is rounded to the currency and the leftover goes to the
    /// largest shop (lowest id on a tie), so the parts add up to the amount exactly.
    /// </summary>
    public static IReadOnlyDictionary<int, decimal> Split(decimal amount, IReadOnlyDictionary<int, decimal> shopSubtotals, int decimalPlaces)
    {
        var total = shopSubtotals.Values.Sum();
        var parts = new Dictionary<int, decimal>();
        if (total <= 0m) return parts;

        foreach (var (vendorId, subtotal) in shopSubtotals.OrderBy(s => s.Key))
            parts[vendorId] = Math.Round(amount * subtotal / total, decimalPlaces, MidpointRounding.AwayFromZero);

        var largest = shopSubtotals.OrderByDescending(s => s.Value).ThenBy(s => s.Key).First().Key;
        parts[largest] += amount - parts.Values.Sum();
        return parts.Where(p => p.Value != 0m).ToDictionary(p => p.Key, p => p.Value);
    }

    public static string MessageOf(string? reason) => reason switch
    {
        DiscountReasons.Disabled => "This code is not active.",
        DiscountReasons.NotStarted => "This code is not valid yet.",
        DiscountReasons.Expired => "This code has expired.",
        DiscountReasons.MinSubtotal => "Your cart is below the minimum amount for this code.",
        DiscountReasons.LimitReached => "This code has been used up.",
        DiscountReasons.CustomerLimitReached => "You have already used this code the allowed number of times.",
        DiscountReasons.NothingToDiscount => "This code gives no discount on your cart.",
        _ => "This code does not exist or does not apply to your cart."
    };
}

public interface IDiscountStore
{
    /// <summary>The discounts of one scope: a shop's, or the platform's when <paramref name="vendorId"/> is null.</summary>
    Task<IReadOnlyList<Discount>> GetListAsync(int? vendorId, CancellationToken cancellationToken);

    Task<int> CountAsync(int? vendorId, CancellationToken cancellationToken);
    Task<Discount?> GetAsync(int id, CancellationToken cancellationToken);
    Task<Discount?> GetByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Adds the discount; 0 (and nothing added) when the code is already used.</summary>
    Task<int> InsertAsync(Discount discount, CancellationToken cancellationToken);

    /// <summary>Writes everything but the owner and the use count; false when the code now belongs to another discount.</summary>
    Task<bool> UpdateAsync(Discount discount, CancellationToken cancellationToken);

    Task<bool> HasUsageAsync(int id, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);

    Task<int> CountCustomerUsesAsync(int discountId, int customerId, CancellationToken cancellationToken);

    /// <summary>
    /// The codes that can be offered for a cart with these shops (see <see cref="DiscountRules.IsOffered"/>), newest first, at most
    /// <see cref="DiscountLimits.MaxOffers"/>.
    /// </summary>
    Task<IReadOnlyList<Discount>> GetOffersAsync(IReadOnlyCollection<int> vendorIds, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>How many times the customer used each of these discounts (discount id to uses; unused ones are left out).</summary>
    Task<IReadOnlyDictionary<int, int>> CountCustomerUsesAsync(IReadOnlyCollection<int> discountIds, int customerId, CancellationToken cancellationToken);

    /// <summary>Locks the discount, checks the usage limits, records the use and counts it, all in one transaction.</summary>
    Task<RedeemOutcome> TryRedeemAsync(RedeemRequest request, CancellationToken cancellationToken);

    /// <summary>Gives back the use of an order. False when the order used no code.</summary>
    Task<bool> ReleaseAsync(int orderId, CancellationToken cancellationToken);
}

public interface IDiscountService
{
    // ---- Scope: a shop (the caller was checked against it) or the platform (vendorId null) ----

    Task<IReadOnlyList<Discount>> GetListAsync(int? vendorId, CancellationToken cancellationToken);
    Task<CatalogResult<Discount>> CreateAsync(int? vendorId, SaveDiscountCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<Discount>> UpdateAsync(int? vendorId, int id, SaveDiscountCommand command, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteAsync(int? vendorId, int id, int actorCustomerId, CancellationToken cancellationToken);

    // ---- Checkout ----

    /// <summary>What a code gives for the subtotals of the shops in the cart (vendor id to subtotal), or why it gives nothing. Changes nothing.</summary>
    Task<CouponCheck> CheckCouponAsync(string? code, int customerId, IReadOnlyDictionary<int, decimal> shopSubtotals, CancellationToken cancellationToken);

    /// <summary>The codes the customer can pick for these subtotals, each with its amount or the reason it cannot be used, ranked. Changes nothing.</summary>
    Task<IReadOnlyList<CouponOffer>> GetOffersAsync(int customerId, IReadOnlyDictionary<int, decimal> shopSubtotals, CancellationToken cancellationToken);

    /// <summary>Records the use of the discount for an order, within its limits.</summary>
    Task<RedeemOutcome> RedeemAsync(AppliedDiscount applied, int customerId, int orderId, CancellationToken cancellationToken);

    /// <summary>Gives back the use of an order whose checkout failed.</summary>
    Task ReleaseAsync(int orderId, CancellationToken cancellationToken);
}
