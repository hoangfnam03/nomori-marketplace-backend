using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Services.Payments;

namespace Nomori.Marketplace.Api.Modules.Payments;

/// <summary>
/// The customer's return from Stripe Checkout. Stripe's webhook goes to the shared callback address (/api/v1/payments/callbacks/stripe);
/// this reads the session the customer paid, so the payment completes even when the webhook is late or, locally, never comes.
/// </summary>
[ApiController]
[Route("api/v1/payments/stripe")]
[AllowAnonymous]
[EnableRateLimiting("callbacks")]
public sealed class StripeController(IPaymentService paymentService, IEnumerable<IPaymentProvider> providers, IOptions<PaymentOptions> options)
    : ControllerBase
{
    [HttpGet("return/{paymentId:int}")]
    public async Task<IActionResult> Return(int paymentId, [FromQuery(Name = "session_id")] string? sessionId, CancellationToken cancellationToken)
    {
        if (providers.OfType<StripePaymentProvider>().FirstOrDefault() is { } stripe && !string.IsNullOrEmpty(sessionId)
            && await stripe.ReadPaidSessionAsync(sessionId, cancellationToken) is { } paid)
            await paymentService.HandleCallbackAsync(StripePaymentProvider.Name, paid.Body, paid.Headers, cancellationToken);

        // Only to the site's own address, never to one taken from the request: the order paid, or the wallet topped up.
        var payment = await paymentService.GetPaymentAsync(paymentId, cancellationToken);
        var landing = payment is null ? "/storefront" : PaymentReferenceTypes.LandingPath(payment);
        return Redirect(options.Value.Stripe.ReturnBaseUrl.TrimEnd('/') + landing);
    }
}
