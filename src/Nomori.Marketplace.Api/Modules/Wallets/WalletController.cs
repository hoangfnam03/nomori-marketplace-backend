using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Wallets;

namespace Nomori.Marketplace.Api.Modules.Wallets;

/// <summary>The signed-in customer's own wallet: the balance, its history, and top-ups. The customer always comes from the session.</summary>
[ApiController]
[Route("api/v1/wallet")]
[Authorize]
public sealed class WalletController(IWalletService wallets, IPaymentService payments, ICurrentUser currentUser) : ControllerBase
{
    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var wallet = await wallets.GetMineAsync(CustomerId, cancellationToken);
        // The gateways with their own payment page are the ones a top-up can go through.
        var topUpMethods = (await payments.GetAvailableMethodsAsync(cancellationToken))
            .Where(m => m.Kind == PaymentProviderKind.Hosted)
            .Select(m => new WalletTopUpMethodResponse(m.SystemName, m.DisplayName))
            .ToList();
        return Ok(new WalletResponse(wallet.Balance, wallet.CurrencyCode, topUpMethods, WalletLimits.MinTopUp, WalletLimits.MaxTopUp));
    }

    [HttpGet("entries")]
    public async Task<IActionResult> Entries([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await wallets.GetEntriesAsync(CustomerId, page, pageSize, cancellationToken);
        return Ok(new WalletEntryPageResponse(
            result.Items.Select(WalletEntryResponse.From).ToList(), result.TotalCount, result.Page, result.PageSize,
            result.PageSize == 0 ? 0 : (int)Math.Ceiling(result.TotalCount / (double)result.PageSize)));
    }

    /// <summary>Starts a top-up: the answer carries the gateway page the customer is sent to. The money is added once the gateway confirms it.</summary>
    [HttpPost("topups")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TopUp(WalletTopUpBody body, CancellationToken cancellationToken)
    {
        var result = await wallets.StartTopUpAsync(CustomerId, new WalletTopUpCommand(body.Amount, body.Method, body.IdempotencyKey), cancellationToken);
        return result.Succeeded
            ? Ok(new WalletTopUpResponse(result.Value!.Id, result.Value.Amount, result.Value.RedirectUrl))
            : this.ToFailure(result);
    }
}

public sealed record WalletResponse(
    decimal Balance, string CurrencyCode, IReadOnlyList<WalletTopUpMethodResponse> TopUpMethods, decimal MinTopUp, decimal MaxTopUp);

public sealed record WalletTopUpMethodResponse(string SystemName, string DisplayName);

public sealed record WalletEntryResponse(
    long Id, string Kind, decimal Amount, decimal BalanceAfter, string? ReferenceType, int? ReferenceId, string? Note, DateTime CreatedOnUtc)
{
    public static WalletEntryResponse From(WalletEntry e) =>
        new(e.Id, WalletRules.ToWire(e.Kind), e.Amount, e.BalanceAfter, e.ReferenceType, e.ReferenceId, e.Note, e.CreatedOnUtc);
}

public sealed record WalletEntryPageResponse(IReadOnlyList<WalletEntryResponse> Items, int TotalCount, int Page, int PageSize, int TotalPages);

public sealed record WalletTopUpBody(decimal Amount, string? Method, string? IdempotencyKey);

public sealed record WalletTopUpResponse(int PaymentId, decimal Amount, string? RedirectUrl);
