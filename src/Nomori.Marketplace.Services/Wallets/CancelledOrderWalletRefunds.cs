using System.Globalization;
using Microsoft.Extensions.Logging;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Wallets;

namespace Nomori.Marketplace.Services.Wallets;

/// <summary>
/// A shop order cancelled by the customer, the shop or an administrator after the order was paid: what the customer paid for that shop order
/// goes to their wallet. The credit is keyed by the shop order, so it happens once. The cancel is already saved and is never undone here: a
/// credit that fails is logged and audited for an administrator.
/// </summary>
public sealed partial class CancelledOrderWalletRefunds(
    IMaintenanceStore maintenance,
    IPaymentStore payments,
    IWalletStore wallets,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<CancelledOrderWalletRefunds> logger) : IOrderCancellationRefunds
{
    public async Task RefundCancelledAsync(ShopOrder shopOrder, OrderActor actor, CancellationToken cancellationToken)
    {
        // The system cancels orders that were never paid (or refunds them through the gateway itself).
        if (actor == OrderActor.System || shopOrder.Order is not { } order || shopOrder.Total <= 0m) return;

        try
        {
            if (!await IsPaidAsync(order.Id, cancellationToken)) return;

            var key = string.Create(CultureInfo.InvariantCulture, $"cancel-{shopOrder.Id}");
            // The wallet store, not the wallet service: the service pays through payments, whose order handler leads back to orders.
            var posted = await wallets.PostAsync(new WalletPosting(
                order.CustomerId, WalletEntryKind.Refund, shopOrder.Total, key, "shop_order", shopOrder.Id, order.Number, clock.UtcNow), cancellationToken);
            if (posted.Outcome != WalletPostOutcome.Posted) return;
            await auditLog.WriteAsync("order.cancel_refunded", null, entityType: "ShopOrder", entityId: shopOrder.Id,
                details: new { shopOrderId = shopOrder.Id, orderId = order.Id, shopOrder.Total, toWallet = true }, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRefundFailed(exception, shopOrder.Id);
            await auditLog.WriteAsync("order.cancel_refund_failed", null, entityType: "ShopOrder", entityId: shopOrder.Id,
                details: new { shopOrderId = shopOrder.Id, orderId = order.Id, shopOrder.Total }, cancellationToken: CancellationToken.None);
        }
    }

    private async Task<bool> IsPaidAsync(int orderId, CancellationToken cancellationToken)
    {
        foreach (var id in await maintenance.PaymentIdsForOrderAsync(orderId, cancellationToken))
        {
            if ((await payments.GetAsync(id, cancellationToken))?.Status is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded) return true;
        }
        return false;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Shop order {ShopOrderId} was cancelled after payment but its money could not go to the wallet.")]
    private partial void LogRefundFailed(Exception exception, int shopOrderId);
}
