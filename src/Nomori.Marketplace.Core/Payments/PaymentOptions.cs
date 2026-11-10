namespace Nomori.Marketplace.Core.Payments;

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>Stripe Checkout. Registered only with a secret key and a webhook secret; both live in the environment (.env), never in git.</summary>
    public StripePaymentOptions Stripe { get; init; } = new();
}

public sealed class StripePaymentOptions
{
    /// <summary>sk_test_… (test mode) or sk_live_….</summary>
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// whsec_… of the webhook endpoint. Locally, without a webhook, any long random string works: the return signs its own events with it.
    /// </summary>
    public string WebhookSecret { get; init; } = string.Empty;

    public string ApiBase { get; init; } = "https://api.stripe.com";

    /// <summary>The site's own address, where Stripe sends the customer back (through the API, then to the order).</summary>
    public string ReturnBaseUrl { get; init; } = string.Empty;

    /// <summary>How long the Checkout page can be paid (Stripe allows 30 minutes to 24 hours).</summary>
    public int ExpireMinutes { get; init; } = 30;

    public bool IsConfigured =>
        SecretKey.StartsWith("sk_", StringComparison.Ordinal) && WebhookSecret.Length >= 16
        && Uri.TryCreate(ApiBase, UriKind.Absolute, out _) && Uri.TryCreate(ReturnBaseUrl, UriKind.Absolute, out var site) && site.Scheme is "http" or "https";
}

