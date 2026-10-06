using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Jobs;

/// <summary>
/// Cancels orders whose payment never came. The order is cancelled first, so stock and the discount use come back at once; the money is looked at
/// afterwards, because a callback may have paid the order between the listing and the cancel, and a cancelled order must never keep customer money.
/// </summary>
public sealed partial class ExpireUnpaidOrdersJob(
    IMaintenanceStore maintenance,
    IOrderService orders,
    IDiscountService discounts,
    IPaymentService payments,
    IClock clock,
    IOptions<JobOptions> options,
    ILogger<ExpireUnpaidOrdersJob> logger) : IScheduledJob
{
    public string Name => "orders.expire_unpaid";
    public string Description => "Cancels orders that are still awaiting payment after the allowed time, and gives back stock and discount use.";
    public int DefaultIntervalMinutes => 5;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddMinutes(-Math.Max(1, options.Value.AwaitingPaymentMinutes));
        var ids = await maintenance.ExpiredAwaitingOrderIdsAsync(cutoff, JobRules.BatchSize, cancellationToken);

        var cancelled = 0;
        var failed = 0;
        foreach (var orderId in ids)
        {
            try
            {
                if (await ExpireAsync(orderId, cancellationToken)) cancelled++;
                else failed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One order that cannot be cancelled must not hold back the rest; it is listed again at the next run.
                LogExpireFailed(exception, orderId);
                failed++;
            }
        }
        return new JobResult(cancelled, failed, ids.Count == 0 ? null : $"Cancelled {cancelled} of {ids.Count} unpaid orders.");
    }

    private async Task<bool> ExpireAsync(int orderId, CancellationToken cancellationToken)
    {
        var cancel = await orders.CancelAsSystemAsync(orderId, "Payment not completed in time", cancellationToken);
        if (!cancel.Succeeded) return false;

        await discounts.ReleaseAsync(orderId, cancellationToken);

        var allRefunded = true;
        foreach (var paymentId in await maintenance.PaymentIdsForOrderAsync(orderId, cancellationToken))
        {
            var payment = await payments.GetPaymentAsync(paymentId, cancellationToken);
            if (payment is null || !PaymentRules.CanRefund(payment.Status)) continue;

            var refund = await payments.RefundAsync(paymentId, PaymentRules.Refundable(payment), 0, cancellationToken);
            if (!refund.Succeeded)
            {
                LogRefundFailed(orderId, paymentId, refund.ErrorCode);
                allRefunded = false;
            }
        }
        return allRefunded;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not expire unpaid order {OrderId}.")]
    private partial void LogExpireFailed(Exception exception, int orderId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Order {OrderId} was cancelled but payment {PaymentId} could not be refunded: {Code}.")]
    private partial void LogRefundFailed(int orderId, int paymentId, string? code);
}

/// <summary>
/// Voids payments that nobody is going to complete: pending or authorized, for an order that is fully cancelled, and quiet for the grace period.
/// Payments of orders that are still alive (cash on delivery in progress) are never listed.
/// </summary>
public sealed partial class VoidStalePaymentsJob(
    IMaintenanceStore maintenance,
    IPaymentService payments,
    IClock clock,
    IOptions<JobOptions> options,
    ILogger<VoidStalePaymentsJob> logger) : IScheduledJob
{
    public string Name => "payments.void_stale";
    public string Description => "Voids pending payments of cancelled orders after a grace period.";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddHours(-Math.Max(1, options.Value.StalePaymentGraceHours));
        var ids = await maintenance.StalePaymentIdsAsync(cutoff, JobRules.BatchSize, cancellationToken);

        var voided = 0;
        var failed = 0;
        foreach (var paymentId in ids)
        {
            try
            {
                var result = await payments.VoidAsync(paymentId, 0, cancellationToken);
                // "Invalid state" means a callback changed the payment while we were here: nothing is left to do for it.
                if (result.Succeeded || result.ErrorCode == PaymentErrors.InvalidState) voided++;
                else
                {
                    LogVoidRefused(paymentId, result.ErrorCode);
                    failed++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogVoidFailed(exception, paymentId);
                failed++;
            }
        }
        return new JobResult(voided, failed, ids.Count == 0 ? null : $"Voided {voided} of {ids.Count} stale payments.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not void stale payment {PaymentId}: {Code}.")]
    private partial void LogVoidRefused(int paymentId, string? code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not void stale payment {PaymentId}.")]
    private partial void LogVoidFailed(Exception exception, int paymentId);
}
