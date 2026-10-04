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

    // An offline method has no gateway to call back.
    public PaymentCallback? ParseCallback(string body, IReadOnlyDictionary<string, string> headers) => null;
}

/// <summary>
/// A test gateway that behaves like a real one: it holds the money at once (authorized) and tells the platform about later changes with
/// callbacks signed with HMAC-SHA256 over the raw body. It exists only when a secret is configured.
/// </summary>
public sealed class SandboxPaymentProvider(string secret) : IPaymentProvider
{
    public const string Name = "sandbox";
    public const string SignatureHeader = "X-Nomori-Signature";

    private readonly byte[] key = Encoding.UTF8.GetBytes(secret);

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

    /// <summary>The signature a caller has to send for this body. Used by tests and by developers sending a callback by hand.</summary>
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
