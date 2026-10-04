using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Orders;

/// <summary>
/// The lifecycle steps nobody clicks (vendor-orders-prd.md, FR-08): an order the shop did not confirm in time is cancelled,
/// a shipped order counts as delivered after some days, and a delivered one is completed. Each step moves an order only while
/// it is still in the expected status, so running twice, or alongside a person acting on the same order, changes nothing twice.
/// </summary>
public sealed class OrderAutomation(
    IOrderStore orderStore,
    ICustomerIdentityStore identityStore,
    IAuditLogService auditLog,
    OrderNotifier notifier,
    IClock clock,
    IOptions<OrderOptions> options) : IOrderAutomation
{
    /// <summary>Orders handled per step and run; the rest wait for the next run.</summary>
    private const int BatchSize = 200;

    public async Task<AutomationResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var settings = options.Value;

        var cancelled = await StepAsync(StoreOrderStatus.Pending, now, StoreOrderStatus.Cancelled, cancellationToken);
        var delivered = await StepAsync(StoreOrderStatus.Shipped, now.AddDays(-settings.AutoDeliverAfterDays), StoreOrderStatus.Delivered, cancellationToken);
        var completed = await StepAsync(StoreOrderStatus.Delivered, now.AddDays(-settings.AutoCompleteAfterDays), StoreOrderStatus.Completed, cancellationToken);
        return new AutomationResult(cancelled, delivered, completed);
    }

    private async Task<int> StepAsync(StoreOrderStatus from, DateTime dueBeforeUtc, StoreOrderStatus to, CancellationToken cancellationToken)
    {
        var moved = 0;
        foreach (var id in await orderStore.FindDueAsync(from, dueBeforeUtc, BatchSize, cancellationToken))
        {
            var found = await orderStore.GetStoreOrderAsync(id, cancellationToken);
            if (found is not { } pair || pair.StoreOrder.Status != from) continue;
            var storeOrder = pair.StoreOrder;

            var cancelling = to == StoreOrderStatus.Cancelled;
            PaymentStatus? payment = to switch
            {
                StoreOrderStatus.Cancelled => OrderRules.PaymentAfterCancel(storeOrder.PaymentStatus),
                StoreOrderStatus.Delivered => PaymentStatus.Paid,
                _ => null
            };
            var applied = await orderStore.TransitionAsync(new StoreOrderTransition(
                storeOrder.Id, from, to, OrderActorType.System, null, cancelling ? SystemCancelReasons.NotConfirmedInTime : null, null,
                payment, clock.UtcNow, Restock: cancelling), cancellationToken);
            if (!applied) continue;
            moved++;

            await auditLog.WriteAsync($"order.auto_{to.ToString().ToLowerInvariant()}", null, entityType: "StoreOrder", entityId: storeOrder.Id,
                details: new { subOrderNumber = storeOrder.SubOrderNumber, from = from.ToString(), to = to.ToString() }, cancellationToken: cancellationToken);

            if (cancelling && await identityStore.FindByIdAsync(pair.CustomerId, cancellationToken) is { } customer)
                await notifier.StatusChangedForCustomerAsync(storeOrder, to, customer.Email, SystemCancelReasons.NotConfirmedInTime, null, null, null, cancellationToken);
        }
        return moved;
    }
}
