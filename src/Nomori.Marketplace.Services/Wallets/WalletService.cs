using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Wallets;

namespace Nomori.Marketplace.Services.Wallets;

public sealed class WalletService(
    IWalletStore store,
    IPaymentService payments,
    IPrimaryCurrencyProvider primaryCurrency,
    IAuditLogService auditLog,
    IClock clock) : IWalletService
{
    public async Task<Wallet> GetMineAsync(int customerId, CancellationToken cancellationToken)
    {
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        return new Wallet(customerId, await store.GetBalanceAsync(customerId, cancellationToken), currency.Code, null);
    }

    public Task<PagedResult<WalletEntry>> GetEntriesAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken) =>
        store.GetEntriesAsync(customerId, Math.Max(1, page), Math.Clamp(pageSize, 1, WalletLimits.MaxPageSize), cancellationToken);

    public async Task<CatalogResult<PaymentTransaction>> StartTopUpAsync(int customerId, WalletTopUpCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (command.Amount is < WalletLimits.MinTopUp or > WalletLimits.MaxTopUp || decimal.Truncate(command.Amount) != command.Amount)
            errors["amount"] = [$"Top up a whole amount from {WalletLimits.MinTopUp:0} to {WalletLimits.MaxTopUp:0}."];
        var key = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (key.Length is < 8 or > 64) errors["idempotencyKey"] = ["The key must have 8 to 64 characters."];
        if (errors.Count > 0) return CatalogResult.Failure<PaymentTransaction>(errors);

        // Only a gateway with its own payment page can top up: the money has to come from outside, and the wallet itself cannot pay for itself.
        var method = (await payments.GetAvailableMethodsAsync(cancellationToken))
            .FirstOrDefault(m => string.Equals(m.SystemName, command.Method?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (method is null || method.Kind != PaymentProviderKind.Hosted) return CatalogResult.Error<PaymentTransaction>(WalletErrors.MethodNotAllowed);

        // Keys of different customers never meet.
        var started = await payments.CreateAsync(new CreatePaymentCommand(
            PaymentReferenceTypes.WalletTopUp, customerId, $"wallet-{customerId}:{key}", method.SystemName, command.Amount, customerId), cancellationToken);
        if (started.Succeeded)
            await auditLog.WriteAsync("wallet.topup_started", customerId, entityType: "PaymentTransaction", entityId: started.Value!.Id,
                details: new { customerId, command.Amount, method = method.SystemName }, cancellationToken: cancellationToken);
        return started;
    }

    public async Task<WalletPostResult> CreditAsync(
        int customerId, WalletEntryKind kind, decimal amount, string idempotencyKey, string? referenceType, int? referenceId, string? note,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m) return new WalletPostResult(WalletPostOutcome.AlreadyPosted, await store.GetBalanceAsync(customerId, cancellationToken));

        var result = await store.PostAsync(
            new WalletPosting(customerId, kind, amount, idempotencyKey, referenceType, referenceId, note, clock.UtcNow), cancellationToken);
        if (result.Outcome == WalletPostOutcome.Posted)
            await auditLog.WriteAsync("wallet.credited", customerId, entityType: "Wallet", entityId: customerId,
                details: new { customerId, kind = WalletRules.ToWire(kind), amount, referenceType, referenceId }, cancellationToken: cancellationToken);
        return result;
    }
}
