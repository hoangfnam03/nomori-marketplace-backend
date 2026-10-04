using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Payments;

/// <summary>The payment methods a customer can be offered. Creating a payment is not a customer route: checkout does it through the service.</summary>
[ApiController]
[Route("api/v1/payment/methods")]
[Authorize]
public sealed class PaymentMethodsController(IPaymentService paymentService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAvailable(CancellationToken cancellationToken) =>
        Ok((await paymentService.GetAvailableMethodsAsync(cancellationToken)).Select(m => new PublicPaymentMethodResponse(m.SystemName, m.DisplayName, m.Kind == PaymentProviderKind.Offline)));
}

[ApiController]
[Route("api/v1/admin")]
[Authorize]
[HasPermission(PermissionCodes.PaymentsManage)]
public sealed class AdminPaymentsController(IPaymentService paymentService, ICurrentUser currentUser) : ControllerBase
{
    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    [HttpGet("payment-methods")]
    public async Task<IActionResult> GetMethods(CancellationToken cancellationToken) =>
        Ok((await paymentService.GetMethodsAsync(cancellationToken)).Select(PaymentMethodResponse.From));

    [HttpPut("payment-methods/{systemName}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateMethod(string systemName, UpdatePaymentMethodRequest request, CancellationToken cancellationToken)
    {
        var result = await paymentService.UpdateMethodAsync(systemName, request.Enabled, request.DisplayOrder, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(PaymentMethodResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpGet("payments")]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        PaymentStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!PaymentRules.TryParseWire(status, out var value)) return this.ToFailure(CatalogResult.Failure<object>("status", "Unknown status."));
            parsed = value;
        }

        var paged = await paymentService.GetPaymentsAsync(new PaymentQuery(parsed, page, pageSize), cancellationToken);
        var totalPages = paged.PageSize == 0 ? 0 : (int)Math.Ceiling(paged.TotalCount / (double)paged.PageSize);
        return Ok(new CatalogPagedResponse<PaymentResponse>(paged.Items.Select(PaymentResponse.From).ToList(), paged.TotalCount, paged.Page, paged.PageSize, totalPages));
    }

    [HttpGet("payments/{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken) =>
        await paymentService.GetPaymentAsync(id, cancellationToken) is { } payment ? Ok(PaymentResponse.From(payment)) : NotFound();

    [HttpPost("payments/{id:int}/capture")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Capture(int id, CancellationToken cancellationToken)
    {
        var result = await paymentService.CaptureAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(PaymentResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPost("payments/{id:int}/void")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Void(int id, CancellationToken cancellationToken)
    {
        var result = await paymentService.VoidAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(PaymentResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPost("payments/{id:int}/refund")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(int id, RefundPaymentRequest request, CancellationToken cancellationToken)
    {
        var result = await paymentService.RefundAsync(id, request.Amount, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(PaymentResponse.From(result.Value!)) : this.ToFailure(result);
    }
}

/// <summary>
/// Where gateways tell the platform what happened. There is no cookie, so no CSRF token: the adapter verifies a signature over the raw body
/// before anything is trusted, and every event id is handled once.
/// </summary>
[ApiController]
[Route("api/v1/payments/callbacks")]
[AllowAnonymous]
[EnableRateLimiting("callbacks")]
public sealed class PaymentCallbackController(IPaymentService paymentService) : ControllerBase
{
    [HttpPost("{provider}")]
    public async Task<IActionResult> Receive(string provider, CancellationToken cancellationToken)
    {
        if (Request.ContentLength > PaymentLimits.MaxCallbackBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        // The limit is checked again while reading: a client can omit or lie about the length.
        var buffer = new byte[PaymentLimits.MaxCallbackBytes + 1];
        var read = 0;
        int count;
        while (read < buffer.Length && (count = await Request.Body.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0) read += count;
        if (read > PaymentLimits.MaxCallbackBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var outcome = await paymentService.HandleCallbackAsync(provider, Encoding.UTF8.GetString(buffer, 0, read), headers, cancellationToken);
        return outcome == CallbackOutcome.Rejected
            ? Unauthorized()
            : Ok(new { outcome = outcome.ToString().ToLowerInvariant() });
    }
}

public sealed record UpdatePaymentMethodRequest(bool Enabled, int DisplayOrder);

public sealed record RefundPaymentRequest(decimal Amount);

public sealed record PublicPaymentMethodResponse(string SystemName, string DisplayName, bool IsOffline);

public sealed record PaymentMethodResponse(string SystemName, string DisplayName, bool IsOffline, bool Enabled, int DisplayOrder, bool Registered)
{
    public static PaymentMethodResponse From(PaymentMethodView view) =>
        new(view.SystemName, view.DisplayName, view.Kind == PaymentProviderKind.Offline, view.Enabled, view.DisplayOrder, view.Registered);
}

public sealed record PaymentResponse(
    int Id, string ReferenceType, int ReferenceId, string Method, int? CustomerId, decimal Amount, string CurrencyCode, string Status,
    decimal RefundedAmount, decimal Refundable, string? ProviderReference, string? FailureCode, DateTime CreatedOnUtc, DateTime UpdatedOnUtc)
{
    public static PaymentResponse From(PaymentTransaction p) => new(
        p.Id, p.ReferenceType, p.ReferenceId, p.Method, p.CustomerId, p.Amount, p.CurrencyCode, PaymentRules.ToWire(p.Status),
        p.RefundedAmount, PaymentRules.CanRefund(p.Status) ? PaymentRules.Refundable(p) : 0m, p.ProviderReference, p.FailureCode, p.CreatedOnUtc, p.UpdatedOnUtc);
}
