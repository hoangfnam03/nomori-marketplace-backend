using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Payments;

/// <summary>
/// Stripe Checkout. Starting a payment creates a Checkout Session; its page is kept with the payment and the customer is sent there. Stripe
/// reports by webhook (signed with the endpoint's secret, header Stripe-Signature); when the customer comes back, the session is also read
/// and written as the same signed event, so a payment completes even where Stripe cannot reach the server (locally). Refunds name the
/// PaymentIntent the session made.
/// </summary>
public sealed class StripePaymentProvider(StripePaymentOptions options, HttpClient http, IClock clock) : IPaymentProvider
{
    public const string Name = "stripe";
    public const string HttpClientName = "stripe";
    public const string SignatureHeader = "Stripe-Signature";

    /// <summary>A signed event older than this is refused, so a captured one cannot be sent again later.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>Currencies Stripe counts in whole units; the others are counted in hundredths.</summary>
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf"
    };

    public string SystemName => Name;
    public string DisplayName => "Thanh toán online (Stripe)";
    public PaymentProviderKind Kind => PaymentProviderKind.Hosted;

    /// <summary>Creates the Checkout Session. Its id is the payment's reference, its page is kept to send the customer to.</summary>
    public async Task<PaymentProviderResult> InitiateAsync(PaymentTransaction transaction, CancellationToken cancellationToken)
    {
        var site = options.ReturnBaseUrl.TrimEnd('/');
        var order = transaction.ReferenceId.ToString(CultureInfo.InvariantCulture);
        var payment = transaction.Id.ToString(CultureInfo.InvariantCulture);
        var what = transaction.ReferenceType == PaymentReferenceTypes.WalletTopUp ? "Wallet top-up" : $"Order #{order}";
        var fields = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = transaction.CurrencyCode.ToLowerInvariant(),
            ["line_items[0][price_data][unit_amount]"] = MinorUnits(transaction.Amount, transaction.CurrencyCode),
            ["line_items[0][price_data][product_data][name]"] = $"Nomori - {what}",
            ["client_reference_id"] = transaction.Id.ToString(CultureInfo.InvariantCulture),
            ["metadata[payment_id]"] = transaction.Id.ToString(CultureInfo.InvariantCulture),
            ["metadata[reference]"] = $"{transaction.ReferenceType}:{order}",
            // Stripe replaces {CHECKOUT_SESSION_ID} itself; the return reads the session by it, then shows what was paid.
            ["success_url"] = $"{site}/api/v1/payments/stripe/return/{payment}?session_id={{CHECKOUT_SESSION_ID}}",
            ["cancel_url"] = site + PaymentReferenceTypes.LandingPath(transaction),
            // Stripe accepts 30 minutes to 24 hours.
            ["expires_at"] = new DateTimeOffset(clock.UtcNow.AddMinutes(Math.Clamp(options.ExpireMinutes, 30, 1440))).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };

        var answer = await SendAsync(HttpMethod.Post, "/v1/checkout/sessions", fields, $"nomori-payment-{transaction.Id}", cancellationToken);
        if (answer is null) return PaymentProviderResult.Fail("stripe_unavailable");
        if (Text(answer, "id") is { } id && Text(answer, "url") is { } url) return new PaymentProviderResult(true, id, RedirectUrl: url);
        return PaymentProviderResult.Fail("stripe_" + (Text(answer["error"] as JsonObject, "code") ?? "refused"));
    }

    // Checkout takes the money when the customer pays: nothing is held to capture, and an unpaid session simply expires.
    public Task<PaymentProviderResult> CaptureAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    public Task<PaymentProviderResult> VoidAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    /// <summary>The Checkout page made for the payment, while it waits for the customer.</summary>
    public string? GetRedirectUrl(PaymentTransaction transaction) =>
        transaction.Status == PaymentStatus.Pending ? transaction.GatewayRedirectUrl : null;

    /// <summary>
    /// A webhook event, once its Stripe-Signature (t=…,v1=…: HMAC-SHA256 of "t.body" with the endpoint secret) is right and recent. A paid
    /// session is a capture, an expired or failed one a failure; any other event is acknowledged and changes nothing.
    /// </summary>
    public PaymentCallback? ParseCallback(string body, IReadOnlyDictionary<string, string> headers)
    {
        var header = headers.FirstOrDefault(h => string.Equals(h.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(header) || !IsSigned(body, header)) return null;

        try
        {
            if (JsonNode.Parse(body) is not JsonObject evt || Text(evt, "id") is not { } eventId || Text(evt, "type") is not { } type) return null;
            if (evt["data"]?["object"] is not JsonObject session || Text(session, "id") is not { } sessionId) return null;

            var paid = type is "checkout.session.completed" or "checkout.session.async_payment_succeeded" && Text(session, "payment_status") == "paid";
            var failed = type is "checkout.session.expired" or "checkout.session.async_payment_failed";
            var callbackType = paid ? PaymentCallbackTypes.Captured : failed ? PaymentCallbackTypes.Failed : "ignored";
            return new PaymentCallback(eventId, callbackType, sessionId, AmountOf(session), Text(session, "payment_intent"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the Checkout Session the customer came back from and, when it is paid, writes the event Stripe would send (signed like a webhook).
    /// Null while it is not paid, or when Stripe cannot be reached.
    /// </summary>
    public async Task<(string Body, IReadOnlyDictionary<string, string> Headers)?> ReadPaidSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!sessionId.StartsWith("cs_", StringComparison.Ordinal) || sessionId.Length > 200) return null;
        var session = await SendAsync(HttpMethod.Get, $"/v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}", null, null, cancellationToken);
        if (session is null || Text(session, "payment_status") != "paid") return null;

        var body = new JsonObject
        {
            ["id"] = $"ret_{sessionId}",
            ["type"] = "checkout.session.completed",
            ["data"] = new JsonObject { ["object"] = session.DeepClone() }
        }.ToJsonString();
        return (body, new Dictionary<string, string> { [SignatureHeader] = SignatureFor(body, clock.UtcNow) });
    }

    /// <summary>Sends money back through Stripe, naming the PaymentIntent of the paid session.</summary>
    public async Task<PaymentProviderResult> RefundAsync(PaymentTransaction transaction, decimal amount, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(transaction.GatewayTransactionId)) return PaymentProviderResult.Fail("stripe_no_payment_intent");

        var fields = new Dictionary<string, string>
        {
            ["payment_intent"] = transaction.GatewayTransactionId,
            ["amount"] = MinorUnits(amount, transaction.CurrencyCode)
        };
        var answer = await SendAsync(HttpMethod.Post, "/v1/refunds", fields, null, cancellationToken);
        return Text(answer, "status") is "succeeded" or "pending"
            ? PaymentProviderResult.Ok(transaction.ProviderReference)
            : PaymentProviderResult.Fail("stripe_refund_" + (Text(answer?["error"] as JsonObject, "code") ?? Text(answer, "status") ?? "failed"));
    }

    // ---- Signatures ----

    /// <summary>The Stripe-Signature header for a body at a time: what Stripe sends, and what the return writes for itself.</summary>
    public string SignatureFor(string body, DateTime utc)
    {
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={timestamp},v1={Sign(options.WebhookSecret, $"{timestamp}.{body}")}";
    }

    private bool IsSigned(string body, string header)
    {
        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0) continue;
            var (key, value) = (part[..at], part[(at + 1)..]);
            if (key == "t") timestamp = value;
            else if (key == "v1") signatures.Add(value);
        }
        if (timestamp is null || signatures.Count == 0 || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        if ((clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime).Duration() > Tolerance) return false;

        // Compared in constant time so the answer does not leak how much of the signature was right.
        var expected = Encoding.ASCII.GetBytes(Sign(options.WebhookSecret, $"{timestamp}.{body}"));
        return signatures.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s.ToLowerInvariant())));
    }

    public static string Sign(string secret, string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    // ---- Plumbing ----

    private async Task<JsonObject?> SendAsync(HttpMethod method, string path, Dictionary<string, string>? fields, string? idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, options.ApiBase.TrimEnd('/') + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
            // The same payment never makes two sessions, even when a request is sent again.
            if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
            if (fields is not null) request.Content = new FormUrlEncodedContent(fields);
            using var response = await http.SendAsync(request, cancellationToken);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string MinorUnits(decimal amount, string currency) =>
        ((long)Math.Round(ZeroDecimal.Contains(currency) ? amount : amount * 100m, 0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    /// <summary>What the session charged, back in the payment's units.</summary>
    private static decimal? AmountOf(JsonObject session)
    {
        if (session["amount_total"] is not JsonValue total || !long.TryParse(total.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return null;
        return ZeroDecimal.Contains(Text(session, "currency") ?? string.Empty) ? minor : minor / 100m;
    }

    private static string? Text(JsonObject? node, string name) => node?[name] is JsonValue value ? value.ToString() : null;
}
