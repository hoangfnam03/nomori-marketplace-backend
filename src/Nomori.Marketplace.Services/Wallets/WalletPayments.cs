using System.Globalization;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Wallets;

namespace Nomori.Marketplace.Services.Wallets;

/// <summary>
/// Paying with the wallet: starting the payment takes the money from the balance at once (paid, nothing to redirect to), or refuses when the
/// balance is short. A refund puts it back. Each payment is taken once, whatever retries happen.
/// </summary>
public sealed class WalletPaymentProvider(IWalletStore wallets, IClock clock) : IPaymentProvider
{
    public const string Name = "wallet";

    public string SystemName => Name;
    public string DisplayName => "Wallet";
    public PaymentProviderKind Kind => PaymentProviderKind.Gateway;

    public async Task<PaymentProviderResult> InitiateAsync(PaymentTransaction transaction, CancellationToken cancellationToken)
    {
        if (transaction.CustomerId is not { } customerId) return PaymentProviderResult.Fail("wallet_no_customer");

        var reference = string.Create(CultureInfo.InvariantCulture, $"wallet-pay-{transaction.Id}");
        var taken = await wallets.PostAsync(new WalletPosting(
            customerId, WalletEntryKind.Payment, -transaction.Amount, reference, transaction.ReferenceType, transaction.ReferenceId,
            null, clock.UtcNow), cancellationToken);
        return taken.Outcome == WalletPostOutcome.Insufficient
            ? PaymentProviderResult.Fail("wallet_insufficient")
            : new PaymentProviderResult(true, reference, PaidNow: true);
    }

    // The money moved when the payment started: nothing is held to capture or void.
    public Task<PaymentProviderResult> CaptureAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    public Task<PaymentProviderResult> VoidAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok(transaction.ProviderReference));

    /// <summary>Puts the money back in the wallet it came from.</summary>
    public async Task<PaymentProviderResult> RefundAsync(PaymentTransaction transaction, decimal amount, CancellationToken cancellationToken)
    {
        if (transaction.CustomerId is not { } customerId) return PaymentProviderResult.Fail("wallet_no_customer");

        // Each refund is its own change; how much may be refunded is the payment service's to check.
        await wallets.PostAsync(new WalletPosting(
            customerId, WalletEntryKind.Refund, amount, $"wallet-refund-{transaction.Id}-{Guid.NewGuid():N}", transaction.ReferenceType,
            transaction.ReferenceId, null, clock.UtcNow), cancellationToken);
        return PaymentProviderResult.Ok(transaction.ProviderReference);
    }

    public string? GetRedirectUrl(PaymentTransaction transaction) => null;

    // The wallet is not a gateway: nothing calls back.
    public PaymentCallback? ParseCallback(string body, IReadOnlyDictionary<string, string> headers) => null;
}

/// <summary>A top-up the gateway reports paid adds its amount to the customer's wallet, once.</summary>
public sealed class WalletTopUpHandler(IWalletStore wallets, IClock clock) : IPaymentOutcomeHandler
{
    public async Task<PaymentFollowUp> OnCallbackAppliedAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        if (payment.ReferenceType != PaymentReferenceTypes.WalletTopUp || payment.Status != PaymentStatus.Paid || payment.CustomerId is not { } customerId)
            return PaymentFollowUp.None;

        await wallets.PostAsync(new WalletPosting(
            customerId, WalletEntryKind.TopUp, payment.Amount, string.Create(CultureInfo.InvariantCulture, $"wallet-topup-{payment.Id}"),
            PaymentReferenceTypes.WalletTopUp, payment.Id, payment.Method, clock.UtcNow), cancellationToken);
        return PaymentFollowUp.None;
    }
}
