using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Services.Payments;

namespace Nomori.Marketplace.Api.Modules.Payments;

/// <summary>
/// The side of the test gateway that a real gateway would run itself: the data of its payment page and the button that says "paid" or "failed".
/// It exists only where the test gateway is configured (a secret and the address of the page) and answers 404 everywhere else. "Complete" does
/// what a real gateway does: it reports the result to the platform with a signed callback, through the same code a real callback goes through.
/// There is no cookie and no CSRF token because the gateway has neither; the payment reference is unguessable and the route is rate limited.
/// </summary>
[ApiController]
[Route("api/v1/payments/sandbox")]
[AllowAnonymous]
[EnableRateLimiting("callbacks")]
public sealed class SandboxGatewayController(IPaymentService paymentService, IEnumerable<IPaymentProvider> providers) : ControllerBase
{
    private HostedSandboxPaymentProvider? Gateway => providers.OfType<HostedSandboxPaymentProvider>().FirstOrDefault();

    [HttpGet("{reference}")]
    public async Task<IActionResult> Get(string reference, CancellationToken cancellationToken)
    {
        if (Gateway is not { } gateway) return NotFound();
        if (await paymentService.FindByProviderReferenceAsync(gateway.SystemName, reference, cancellationToken) is not { } payment) return NotFound();

        return Ok(new SandboxPaymentResponse(payment.Amount, payment.CurrencyCode, PaymentRules.ToWire(payment.Status), gateway.ReturnUrl(payment)));
    }

    [HttpPost("{reference}/complete")]
    public async Task<IActionResult> Complete(string reference, SandboxCompleteRequest request, CancellationToken cancellationToken)
    {
        if (Gateway is not { } gateway) return NotFound();
        var type = request.Outcome switch
        {
            "paid" => PaymentCallbackTypes.Captured,
            "failed" => PaymentCallbackTypes.Failed,
            _ => null
        };
        if (type is null) return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["outcome"] = ["The outcome is paid or failed."] }));
        if (await paymentService.FindByProviderReferenceAsync(gateway.SystemName, reference, cancellationToken) is not { } payment) return NotFound();

        var (body, headers) = gateway.BuildCallback(type, reference);
        var outcome = await paymentService.HandleCallbackAsync(gateway.SystemName, body, headers, cancellationToken);

        var current = await paymentService.FindByProviderReferenceAsync(gateway.SystemName, reference, cancellationToken);
        return Ok(new SandboxCompleteResponse(outcome.ToString().ToLowerInvariant(), PaymentRules.ToWire(current!.Status), gateway.ReturnUrl(payment)));
    }
}

public sealed record SandboxCompleteRequest(string? Outcome);

public sealed record SandboxPaymentResponse(decimal Amount, string CurrencyCode, string Status, string ReturnUrl);

public sealed record SandboxCompleteResponse(string CallbackOutcome, string Status, string ReturnUrl);
