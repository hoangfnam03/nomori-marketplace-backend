using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;

namespace Nomori.Marketplace.Services.Orders;

/// <summary>
/// What an order does when the gateway reports on its payment. Paid: the order stops waiting and shops can see it. Failed or cancelled at the
/// gateway: the order is cancelled, stock goes back and the discount use is given back. Paid for an order that was cancelled meanwhile: the money
/// is refunded in full. Every step is safe to repeat, because a gateway repeats a callback it got no answer to.
/// </summary>
public sealed class OrderPaymentOutcomeHandler(IOrderService orderService, IDiscountService discountService) : IPaymentOutcomeHandler
{
    public async Task<PaymentFollowUp> OnCallbackAppliedAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        // Payments of other things (nothing else exists yet) are not the business of orders.
        if (payment.ReferenceType != PaymentReferenceTypes.Order) return PaymentFollowUp.None;

        var found = await orderService.GetOrderAsync(payment.ReferenceId, cancellationToken);
        if (!found.Succeeded) return PaymentFollowUp.None;
        var order = found.Value!.Order;

        switch (payment.Status)
        {
            case PaymentStatus.Paid:
                // The customer cancelled while paying: there is nothing to ship, so the money goes back.
                if (OrderRules.Overall(order.ShopOrders.Select(s => s.Status)) == OverallOrderStatus.Cancelled) return PaymentFollowUp.Refund;
                await orderService.MarkPaidAsync(order.Id, cancellationToken);
                return PaymentFollowUp.None;

            case PaymentStatus.Failed or PaymentStatus.Voided:
                await orderService.CancelAsSystemAsync(order.Id, "Payment failed", cancellationToken);
                await discountService.ReleaseAsync(order.Id, cancellationToken);
                return PaymentFollowUp.None;

            default:
                return PaymentFollowUp.None;
        }
    }
}
