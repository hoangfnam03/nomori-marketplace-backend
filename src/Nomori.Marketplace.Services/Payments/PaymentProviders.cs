using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nomori.Marketplace.Core.Payments;

namespace Nomori.Marketplace.Services.Payments;

/// <summary>Pay on delivery: nothing is asked of anybody. The payment stays pending until an administrator records that the cash arrived.</summary>
public sealed class CashOnDeliveryProvider : IPaymentProvider
{
    public const string Name = "cod";

    public string SystemName => Name;
    public string DisplayName => "Pay on delivery";
    public PaymentProviderKind Kind => PaymentProviderKind.Offline;

    public Task<PaymentProviderResult> InitiateAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok());

    public Task<PaymentProviderResult> CaptureAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok());

    public Task<PaymentProviderResult> VoidAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok());

    // The money went back by hand; the platform only records it.
    public Task<PaymentProviderResult> RefundAsync(PaymentTransaction transaction, decimal amount, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok());

    public string? GetRedirectUrl(PaymentTransaction transaction) => null;

    // An offline method has no gateway to call back.
    public PaymentCallback? ParseCallback(string body, IReadOnlyDictionary<string, string> headers) => null;
}

/// <summary>
/// What the test gateways share: callbacks signed with HMAC-SHA256 over the raw body, checked in constant time, and a small JSON event
/// (<c>id</c>, <c>type</c>, <c>reference</c>). A real gateway has its own signature scheme in its own adapter.
/// </summary>
public abstract class SignedCallbackProvider(string secret)
{
    public const string SignatureHeader = "X-Nomori-Signature";

    private readonly byte[] key = Encoding.UTF8.GetBytes(secret);

    /// <summary>The signature a caller has to send for this body. Used by tests and by the test gateway page when it plays the gateway.</summary>
    public string Sign(string body) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public PaymentCallback? ParseCallback(string body, IReadOnlyDictionary<string, string> headers)
    {
        var signature = headers.FirstOrDefault(h => string.Equals(h.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(signature)) return null;

        // Compared in constant time so the answer does not leak how much of the signature was right.
        var expected = Encoding.ASCII.GetBytes(Sign(body));
        var given = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var id = ReadString(root, "id");
            var type = ReadString(root, "type");
            var reference = ReadString(root, "reference");
            return id is null || type is null || reference is null ? null : new PaymentCallback(id, type, reference);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 200 } text
            ? text
            : null;
}

/// <summary>
/// A test gateway that holds the money at once (authorized) and tells the platform about later changes with signed callbacks.
/// It exists only when a secret is configured.
/// </summary>
public sealed class SandboxPaymentProvider(string secret) : SignedCallbackProvider(secret), IPaymentProvider
{
    public const string Name = "sandbox";

    public string SystemName => Name;
    public string DisplayName => "Test gateway";
    public PaymentProviderKind Kind => PaymentProviderKind.Gateway;

    public Task<PaymentProviderResult> InitiateAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok("sbx_" + Guid.NewGuid().ToString("N")));

    public Task<PaymentProviderResult> CaptureAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    public Task<PaymentProviderResult> VoidAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    public Task<PaymentProviderResult> RefundAsync(PaymentTransaction transaction, decimal amount, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    public string? GetRedirectUrl(PaymentTransaction transaction) => null;
}

/// <summary>
/// A test gateway with a payment page of its own, like a real hosted gateway: the customer is sent to the page, pays or fails there, and the
/// gateway reports the result by a signed callback. The page lives in the Angular app (<paramref name="pageBaseUrl"/>); it exists only when
/// a secret and that address are configured.
/// </summary>
public sealed class HostedSandboxPaymentProvider(string secret, string pageBaseUrl) : SignedCallbackProvider(secret), IPaymentProvider
{
    public const string Name = "sandbox_redirect";

    private readonly string baseUrl = pageBaseUrl.TrimEnd('/');

    public string SystemName => Name;
    public string DisplayName => "Test gateway with payment page";
    public PaymentProviderKind Kind => PaymentProviderKind.Hosted;

    // The customer has not paid yet: the payment stays pending until the gateway reports.
    public Task<PaymentProviderResult> InitiateAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok("sbr_" + Guid.NewGuid().ToString("N")));

    public Task<PaymentProviderResult> CaptureAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    public Task<PaymentProviderResult> VoidAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    public Task<PaymentProviderResult> RefundAsync(PaymentTransaction transaction, decimal amount, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    /// <summary>The payment page, for as long as the payment waits for the customer.</summary>
    public string? GetRedirectUrl(PaymentTransaction transaction) =>
        transaction.Status == PaymentStatus.Pending && !string.IsNullOrEmpty(transaction.ProviderReference)
            ? $"{baseUrl}/payments/sandbox/{Uri.EscapeDataString(transaction.ProviderReference)}"
            : null;

    /// <summary>Where the gateway sends the customer back to: the order that was being paid.</summary>
    public string ReturnUrl(PaymentTransaction transaction) =>
        transaction.ReferenceType == PaymentReferenceTypes.Order
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseUrl}/customer/orders/{transaction.ReferenceId}?payment=return")
            : $"{baseUrl}/storefront";

    /// <summary>The body and headers of the callback this gateway sends for a result. The event id is new every time.</summary>
    public (string Body, IReadOnlyDictionary<string, string> Headers) BuildCallback(string type, string reference)
    {
        var body = JsonSerializer.Serialize(new { id = "evt_" + Guid.NewGuid().ToString("N"), type, reference });
        return (body, new Dictionary<string, string> { [SignatureHeader] = Sign(body) });
    }
}
