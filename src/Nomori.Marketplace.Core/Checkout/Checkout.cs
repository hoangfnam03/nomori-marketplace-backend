using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Tax;

namespace Nomori.Marketplace.Core.Checkout;

public static class CheckoutLimits
{
    public const int MinKeyLength = 8;
    public const int MaxKeyLength = 64;
    public const int MaxNoteLength = OrderLimits.MaxNoteLength;

    /// <summary>The most units of one line that checkout reserves; it is the limit of one stock reservation.</summary>
    public const int MaxLineQuantity = InventoryLimits.MaxReservationQuantity;

    /// <summary>How long the stock of a checkout is held while the order is made. It is committed in the same request.</summary>
    public const int ReservationMinutes = 15;
}

/// <summary>Business-rule codes of checkout (HTTP 409). Stock problems use <see cref="CatalogErrors.InsufficientStock"/>.</summary>
public static class CheckoutErrors
{
    public const string CartNotReady = "checkout.cart_not_ready";
    public const string PricesChanged = "checkout.prices_changed";
    public const string PaymentFailed = "checkout.payment_failed";

    /// <summary>The code passed the check but its limits ran out before the order could use it.</summary>
    public const string CouponUnavailable = "checkout.coupon_unavailable";
}

/// <summary>What can be wrong with the choices or the cart at checkout. Each one has a stable code for the screen.</summary>
public static class CheckoutProblems
{
    public const string CartEmpty = "cart_empty";
    public const string CartIssues = "cart_issues";
    public const string PricesChanged = "prices_changed";
    public const string AddressRequired = "address_required";
    public const string AddressInvalid = "address_invalid";
    public const string ShippingUnavailable = "shipping_unavailable";
    public const string ShippingNotChosen = "shipping_not_chosen";
    public const string ShippingInvalid = "shipping_invalid";
    public const string PaymentRequired = "payment_required";
    public const string PaymentInvalid = "payment_invalid";
    public const string CouponInvalid = "coupon_invalid";
}

/// <summary>The customer's choice of one shipping option for one shop of the cart.</summary>
public sealed record ShippingChoice(int VendorId, int RateId);

public sealed record CheckoutChoices(int? AddressId, IReadOnlyList<ShippingChoice>? ShippingChoices, string? PaymentMethod, string? CouponCode = null);

public sealed record PlaceOrderRequest(
    int? AddressId, IReadOnlyList<ShippingChoice>? ShippingChoices, string? PaymentMethod, string? IdempotencyKey, bool AcceptedTerms, string? Note,
    string? CouponCode = null);

/// <summary>The shipping side of one shop: what it offers for the address and what the customer chose.</summary>
public sealed record CheckoutShopShipping(
    int VendorId, string? VendorName, decimal Subtotal, IReadOnlyList<ShippingOption> Options, ShippingOption? Chosen);

/// <summary>The discount a code gives: who funds it, how much, and how it falls on each shop of the cart.</summary>
public sealed record CheckoutDiscount(
    string Code, string Name, DiscountFunding Funding, decimal Amount, IReadOnlyDictionary<int, decimal> Split);

/// <summary>The tax of the cart for the chosen address: in all, and per shop (vendor id to tax).</summary>
public sealed record CheckoutTax(decimal Total, IReadOnlyDictionary<int, decimal> PerShop);

public sealed record CheckoutPreview(
    CartView Cart,
    int? AddressId,
    IReadOnlyList<CheckoutShopShipping> Shops,
    IReadOnlyList<PaymentMethodView> PaymentMethods,
    string? PaymentMethod,
    decimal Subtotal,
    decimal? ShippingTotal,
    decimal? Total,
    IReadOnlyList<string> Problems,
    bool CanPlace,
    CheckoutDiscount? Discount = null,
    string? CouponReason = null,
    CheckoutTax? Tax = null);

public sealed record PlacedOrder(Order Order, PaymentTransaction? Payment, bool Replayed);

/// <summary>Whether an order waits for its payment, how the payment stands, and where to pay when it is still pending.</summary>
public sealed record OrderPaymentInfo(bool AwaitingPayment, PaymentStatus? PaymentStatus, string? RedirectUrl);

public static class CheckoutRules
{
    /// <summary>A key made by the browser: letters, digits, "-" and "_", 8 to 64 characters.</summary>
    public static bool IsValidKey(string? key) =>
        key is { Length: >= CheckoutLimits.MinKeyLength and <= CheckoutLimits.MaxKeyLength } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Keys of different customers never meet, so one customer cannot replay or block another's checkout.</summary>
    public static string PlacementKey(int customerId, string key) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{customerId}:{key}");

    /// <summary>The key of the payment of an order, so asking for it again returns the same payment.</summary>
    public static string PaymentKey(int orderId) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"order-{orderId}");

    /// <summary>The stock reference of one checkout.</summary>
    public static string StockReference(string placementKey) => "checkout:" + placementKey;

    /// <summary>Problems about the cart as such (409) rather than about what the customer chose (400).</summary>
    public static bool IsCartProblem(string problem) => problem is CheckoutProblems.CartEmpty or CheckoutProblems.CartIssues or CheckoutProblems.PricesChanged;

    /// <summary>The cart problems of a priced cart: empty, blocked lines, and prices the customer has not accepted.</summary>
    public static IEnumerable<string> CartProblems(CartView cart)
    {
        if (cart.Groups.Count == 0)
        {
            yield return CheckoutProblems.CartEmpty;
            yield break;
        }

        var lines = cart.Groups.SelectMany(g => g.Lines).ToList();
        if (lines.Any(l => l.Issues.Any(CartRules.IsBlocking))) yield return CheckoutProblems.CartIssues;
        if (lines.Any(l => l.Issues.Contains(CartIssues.PriceChanged))) yield return CheckoutProblems.PricesChanged;
    }

    /// <summary>The field of the request a choice problem belongs to.</summary>
    public static string FieldOf(string problem) => problem switch
    {
        CheckoutProblems.AddressRequired or CheckoutProblems.AddressInvalid => "addressId",
        CheckoutProblems.ShippingUnavailable or CheckoutProblems.ShippingNotChosen or CheckoutProblems.ShippingInvalid => "shippingChoices",
        CheckoutProblems.CouponInvalid => "couponCode",
        _ => "paymentMethod"
    };

    public static string MessageOf(string problem) => problem switch
    {
        CheckoutProblems.AddressRequired => "Choose an address to deliver to.",
        CheckoutProblems.AddressInvalid => "We cannot ship to this address. Choose another one.",
        CheckoutProblems.ShippingUnavailable => "A shop in your cart does not ship to this address.",
        CheckoutProblems.ShippingNotChosen => "Choose a shipping option for every shop.",
        CheckoutProblems.ShippingInvalid => "A chosen shipping option is not available for this address.",
        CheckoutProblems.CouponInvalid => "This code cannot be used.",
        CheckoutProblems.PaymentRequired => "Choose a payment method.",
        _ => "This payment method is not available."
    };
}

public interface ICheckoutService
{
    /// <summary>The cart, the options for the chosen address and the totals, with every problem found. Changes nothing.</summary>
    Task<CheckoutPreview> PreviewAsync(int customerId, CheckoutChoices choices, CancellationToken cancellationToken);

    /// <summary>
    /// Checks everything again, takes the stock, creates the order and its payment and empties the cart.
    /// The same idempotency key returns the order it made.
    /// </summary>
    Task<CatalogResult<PlacedOrder>> PlaceAsync(int customerId, PlaceOrderRequest request, CancellationToken cancellationToken);

    /// <summary>The payment state of one of the customer's own orders; another customer's order is not found.</summary>
    Task<CatalogResult<OrderPaymentInfo>> GetPaymentStatusAsync(int customerId, int orderId, CancellationToken cancellationToken);
}
