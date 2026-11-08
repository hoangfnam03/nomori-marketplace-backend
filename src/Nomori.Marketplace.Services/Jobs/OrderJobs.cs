using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Jobs;

/// <summary>
/// Completes shop orders the customer never confirmed: delivered more than <see cref="JobOptions.AutoCompleteDays"/> days ago and with no return
/// in progress. Completing ends the time to ask for a return and lets settlement pay the shop. The order service checks the status and the
/// returns again, so an order the customer confirmed or started returning meanwhile is simply left alone.
/// </summary>
public sealed partial class CompleteDeliveredOrdersJob(
    IMaintenanceStore maintenance,
    IOrderService orders,
    IClock clock,
    IOptions<JobOptions> options,
    ILogger<CompleteDeliveredOrdersJob> logger) : IScheduledJob
{
    public string Name => "orders.complete_delivered";
    public string Description => "Completes delivered orders the customer has not confirmed after the allowed days, unless a return is in progress.";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var days = Math.Max(1, options.Value.AutoCompleteDays);
        var ids = await maintenance.DeliveredShopOrderIdsAsync(clock.UtcNow.AddDays(-days), JobRules.BatchSize, cancellationToken);

        var completed = 0;
        var failed = 0;
        foreach (var shopOrderId in ids)
        {
            try
            {
                var result = await orders.CompleteAsSystemAsync(shopOrderId, $"Completed automatically {days} days after delivery", cancellationToken);
                if (result.Succeeded) completed++;
                // Confirmed by the customer or returned meanwhile: nothing to do, and nothing wrong.
                else if (result.ErrorCode is not (OrderErrors.InvalidTransition or OrderErrors.ReturnOpen)) failed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One order that cannot be completed must not hold back the rest; it is listed again at the next run.
                LogCompleteFailed(exception, shopOrderId);
                failed++;
            }
        }
        return new JobResult(completed, failed, ids.Count == 0 ? null : $"Completed {completed} of {ids.Count} delivered orders.");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not complete delivered shop order {ShopOrderId}.")]
    private partial void LogCompleteFailed(Exception exception, int shopOrderId);
}
