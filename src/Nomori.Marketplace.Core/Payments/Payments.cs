using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Payments;

public static class PaymentLimits
{
    public const decimal MaxAmount = 1_000_000_000m;
    public const int MaxIdempotencyKeyLength = 100;
    public const int MaxReferenceTypeLength = 30;
    public const int MaxCallbackBytes = 64 * 1024;
}

/// <summary>Business-rule codes of payments (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class PaymentErrors
{
    public const string MethodUnavailable = "payment.method_unavailable";
    public const string IdempotencyConflict = "payment.idempotency_conflict";
    public const string InvalidState = "payment.invalid_state";
    public const string RefundExceeds = "payment.refund_exceeds";
    public const string ProviderFailed = "payment.provider_failed";
}

public static class PaymentReferenceTypes
{
    public const string Checkout = "checkout";
    public const string Order = "order";
}

/// <summary>The stored value is part of the database contract: add at the end, never renumber.</summary>
public enum PaymentStatus
{
    Pending = 0,
    Authorized = 1,
    Paid = 2,
    PartiallyRefunded = 3,
    Refunded = 4,
    Voided = 5,
    Failed = 6
}

public enum PaymentProviderKind
{
    /// <summary>Money changes hands outside the platform (cash on delivery, bank transfer).</summary>
    Offline = 0,

    /// <summary>A gateway that answers for itself and may call back.</summary>
    Gateway = 1,

    /// <summary>A gateway with its own payment page: the customer is sent there and the result comes back later by a callback.</summary>
    Hosted = 2
}

public sealed class PaymentTransaction
{
    public int Id { get; set; }
    public string ReferenceType { get; set; } = string.Empty;
    public int ReferenceId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public int? CustomerId { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public decimal RefundedAmount { get; set; }

    /// <summary>The gateway's own id for the payment.</summary>
    public string? ProviderReference { get; set; }

    /// <summary>A short code (never a gateway message) saying why the payment failed.</summary>
    public string? FailureCode { get; set; }

    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }

    /// <summary>Where the customer pays, for a hosted payment that is still pending. Worked out when asked, never stored.</summary>
    public string? RedirectUrl { get; set; }
}

/// <summary>Which methods customers may be offered. A method also has to be registered as a provider.</summary>
public sealed class PaymentMethodSetting
{
    public string SystemName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public sealed record PaymentMethodView(string SystemName, string DisplayName, PaymentProviderKind Kind, bool Enabled, int DisplayOrder, bool Registered);

public sealed record CreatePaymentCommand(
    string? ReferenceType, int ReferenceId, string? IdempotencyKey, string? Method, decimal Amount, int? CustomerId);

public sealed record PaymentQuery(PaymentStatus? Status, int Page, int PageSize);

// ---- Providers ----

/// <summary>What a provider answers. The provider never decides the status: the service maps this through <see cref="PaymentRules"/>.</summary>
public sealed record PaymentProviderResult(bool Succeeded, string? ProviderReference = null, string? FailureCode = null)
{
    public static PaymentProviderResult Ok(string? providerReference = null) => new(true, providerReference);
    public static PaymentProviderResult Fail(string failureCode) => new(false, null, failureCode);
}

/// <summary>An event from a gateway whose signature has been verified. <see cref="Type"/> is one of <see cref="PaymentCallbackTypes"/>.</summary>
public sealed record PaymentCallback(string EventId, string Type, string ProviderReference);

public static class PaymentCallbackTypes
{
    public const string Captured = "captured";
    public const string Failed = "failed";
    public const string Voided = "voided";
}

/// <summary>An adapter to one payment gateway or offline method. Core code only ever talks to this interface.</summary>
public interface IPaymentProvider
{
    string SystemName { get; }
    string DisplayName { get; }
    PaymentProviderKind Kind { get; }

    /// <summary>Starts the payment. A gateway that holds the money returns success and the payment becomes authorized; an offline method leaves it pending.</summary>
    Task<PaymentProviderResult> InitiateAsync(PaymentTransaction transaction, CancellationToken cancellationToken);

    Task<PaymentProviderResult> CaptureAsync(PaymentTransaction transaction, CancellationToken cancellationToken);
    Task<PaymentProviderResult> VoidAsync(PaymentTransaction transaction, CancellationToken cancellationToken);
    Task<PaymentProviderResult> RefundAsync(PaymentTransaction transaction, decimal amount, CancellationToken cancellationToken);

    /// <summary>The page where the customer pays, while a hosted payment is pending; null for every other provider and state.</summary>
    string? GetRedirectUrl(PaymentTransaction transaction);

    /// <summary>
    /// Verifies the signature over the raw body and reads the event. Null when the signature is missing or wrong, or the body is not an event
    /// this provider understands. Headers are looked up case-insensitively.
    /// </summary>
    PaymentCallback? ParseCallback(string body, IReadOnlyDictionary<string, string> headers);
}

/// <summary>What the payment service has to do after a handler has dealt with a callback.</summary>
public enum PaymentFollowUp
{
    None = 0,

    /// <summary>Give all the money back: it arrived for something that no longer exists.</summary>
    Refund = 1
}

/// <summary>
/// Told when a gateway callback changed a payment (or repeated one that did). The order side implements it, so payments do not depend on
/// orders. A handler has to be safe to run more than once for the same payment.
/// </summary>
public interface IPaymentOutcomeHandler
{
    Task<PaymentFollowUp> OnCallbackAppliedAsync(PaymentTransaction payment, CancellationToken cancellationToken);
}

// ---- Rules ----

public static class PaymentRules
{
    public static bool IsFinal(PaymentStatus status) =>
        status is PaymentStatus.Voided or PaymentStatus.Failed or PaymentStatus.Refunded;

    public static bool CanCapture(PaymentStatus status) => status is PaymentStatus.Pending or PaymentStatus.Authorized;

    public static bool CanVoid(PaymentStatus status) => status is PaymentStatus.Pending or PaymentStatus.Authorized;

    public static bool CanRefund(PaymentStatus status) => status is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded;

    /// <summary>What can still be refunded.</summary>
    public static decimal Refundable(PaymentTransaction payment) => payment.Amount - payment.RefundedAmount;

    /// <summary>The status after refunding <paramref name="amount"/> more. The whole amount refunded is final.</summary>
    public static PaymentStatus AfterRefund(decimal paid, decimal alreadyRefunded, decimal amount) =>
        alreadyRefunded + amount >= paid ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;

    /// <summary>The status a gateway event asks for and the status it needs the payment to be in. Null for an event type nobody knows.</summary>
    public static (PaymentStatus Target, Func<PaymentStatus, bool> From)? ForCallback(string type) => type switch
    {
        PaymentCallbackTypes.Captured => (PaymentStatus.Paid, CanCapture),
        PaymentCallbackTypes.Voided => (PaymentStatus.Voided, CanVoid),
        PaymentCallbackTypes.Failed => (PaymentStatus.Failed, s => s is PaymentStatus.Pending or PaymentStatus.Authorized),
        _ => null
    };

    public static string ToWire(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => "pending",
        PaymentStatus.Authorized => "authorized",
        PaymentStatus.Paid => "paid",
        PaymentStatus.PartiallyRefunded => "partially_refunded",
        PaymentStatus.Refunded => "refunded",
        PaymentStatus.Voided => "voided",
        _ => "failed"
    };

    public static bool TryParseWire(string? value, out PaymentStatus status)
    {
        foreach (var candidate in Enum.GetValues<PaymentStatus>())
        {
            if (string.Equals(ToWire(candidate), value, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }
        status = default;
        return false;
    }
}

// ---- Store and service ----

public interface IPaymentStore
{
    Task<IReadOnlyList<PaymentMethodSetting>> GetMethodSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Returns false when there is no such method row.</summary>
    Task<bool> UpdateMethodSettingAsync(PaymentMethodSetting setting, CancellationToken cancellationToken);

    Task<PaymentTransaction?> GetAsync(int id, CancellationToken cancellationToken);
    Task<PaymentTransaction?> GetByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task<PaymentTransaction?> GetByProviderReferenceAsync(string method, string providerReference, CancellationToken cancellationToken);

    /// <summary>Adds the payment. Returns 0 (and adds nothing) when the idempotency key is already used.</summary>
    Task<int> InsertAsync(PaymentTransaction payment, CancellationToken cancellationToken);

    /// <summary>
    /// Compare-and-set: changes the payment only while it is still in <paramref name="expected"/>. False means another request got there first.
    /// </summary>
    Task<bool> TryChangeAsync(
        int id, PaymentStatus expected, PaymentStatus status, decimal refundedAmount, string? providerReference, string? failureCode,
        DateTime nowUtc, CancellationToken cancellationToken);

    Task<PagedResult<PaymentTransaction>> GetPagedAsync(PaymentQuery query, CancellationToken cancellationToken);

    /// <summary>Stores a gateway event. False when this provider already sent this event id (a replay).</summary>
    Task<bool> TryAddEventAsync(string provider, string eventId, string type, int? transactionId, string outcome, DateTime nowUtc, CancellationToken cancellationToken);
}

public interface IPaymentService
{
    // ---- Customers ----

    /// <summary>Enabled and registered methods that can take this amount, in display order.</summary>
    Task<IReadOnlyList<PaymentMethodView>> GetAvailableMethodsAsync(CancellationToken cancellationToken);

    // ---- Checkout (service only: no customer route) ----

    Task<CatalogResult<PaymentTransaction>> CreateAsync(CreatePaymentCommand command, CancellationToken cancellationToken);

    // ---- Administrators ----

    Task<IReadOnlyList<PaymentMethodView>> GetMethodsAsync(CancellationToken cancellationToken);
    Task<CatalogResult<PaymentMethodView>> UpdateMethodAsync(string systemName, bool enabled, int displayOrder, int actorCustomerId, CancellationToken cancellationToken);

    Task<PagedResult<PaymentTransaction>> GetPaymentsAsync(PaymentQuery query, CancellationToken cancellationToken);
    Task<PaymentTransaction?> GetPaymentAsync(int id, CancellationToken cancellationToken);

    Task<CatalogResult<PaymentTransaction>> CaptureAsync(int id, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<PaymentTransaction>> VoidAsync(int id, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<PaymentTransaction>> RefundAsync(int id, decimal amount, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>The page where the customer pays a pending hosted payment; null otherwise.</summary>
    string? GetRedirectUrl(PaymentTransaction payment);

    Task<PaymentTransaction?> FindByProviderReferenceAsync(string method, string providerReference, CancellationToken cancellationToken);

    // ---- Gateways ----

    /// <summary>
    /// Handles a callback. <see cref="CallbackOutcome.Rejected"/> for an unknown provider or a signature that does not verify;
    /// otherwise the event is stored once and applied only when the state machine allows it.
    /// </summary>
    Task<CallbackOutcome> HandleCallbackAsync(string provider, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}

public enum CallbackOutcome
{
    Rejected = 0,
    Applied = 1,
    Ignored = 2,
    Replay = 3
}
