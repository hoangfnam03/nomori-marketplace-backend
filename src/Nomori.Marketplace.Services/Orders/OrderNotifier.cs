using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Orders;

namespace Nomori.Marketplace.Services.Orders;

/// <summary>
/// Queues the order emails of F22-A. It reads the customer's address at the time of the change, writes plain, encoded HTML (no link, no secret,
/// no payment detail) and puts it in the email queue. Everything is best effort: a failure is logged and never reaches the order.
/// </summary>
public sealed partial class OrderNotifier(
    ICustomerIdentityStore customers,
    IEmailQueueService queue,
    IOptions<EmailOptions> options,
    ILogger<OrderNotifier> logger) : IOrderNotifier
{
    public async Task OrderPlacedAsync(Order order, CancellationToken cancellationToken)
    {
        try
        {
            var body = new StringBuilder();
            body.Append("<p>Thank you for your order <strong>").Append(Html(order.Number)).Append("</strong>.</p>");
            foreach (var shop in order.ShopOrders)
            {
                body.Append("<h3>").Append(Html(shop.ShopName)).Append("</h3>");
                AppendLines(body, shop.Lines);
                body.Append("<p>Shipping: ").Append(Html(shop.ShippingMethodName)).Append(" (")
                    .Append(Money(shop.ShippingFee, order.CurrencyCode)).Append(")<br/>Shop total: ")
                    .Append(Money(shop.Total, order.CurrencyCode)).Append("</p>");
            }
            body.Append("<p><strong>Total: ").Append(Money(order.Total, order.CurrencyCode)).Append("</strong><br/>Payment: ")
                .Append(Html(order.PaymentMethod)).Append("</p><p>Delivery to ").Append(Html(Recipient(order))).Append(".</p>");

            await EnqueueAsync(order.CustomerId, EmailKinds.OrderPlaced, options.Value.OrderPlacedSubject, order.Number, body.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, order.Number);
        }
    }

    public async Task ShopOrderChangedAsync(ShopOrder shopOrder, CancellationToken cancellationToken)
    {
        try
        {
            if (shopOrder.Order is null) return;
            var settings = options.Value;
            var shop = Html(shopOrder.ShopName);
            var number = Html(shopOrder.Number);
            var body = new StringBuilder();

            string kind;
            string subject;
            switch (shopOrder.Status)
            {
                case ShopOrderStatus.Shipped:
                    (kind, subject) = (EmailKinds.OrderShipped, settings.OrderShippedSubject);
                    body.Append("<p>Your order <strong>").Append(number).Append("</strong> from ").Append(shop).Append(" is on its way.</p>");
                    if (!string.IsNullOrWhiteSpace(shopOrder.Carrier))
                        body.Append("<p>Carrier: ").Append(Html(shopOrder.Carrier)).Append("<br/>Tracking number: ").Append(Html(shopOrder.TrackingNumber)).Append("</p>");
                    break;
                case ShopOrderStatus.Delivered:
                    (kind, subject) = (EmailKinds.OrderDelivered, settings.OrderDeliveredSubject);
                    body.Append("<p>Your order <strong>").Append(number).Append("</strong> from ").Append(shop)
                        .Append(" was delivered. Please confirm that you received it in your account.</p>");
                    break;
                case ShopOrderStatus.Cancelled:
                    (kind, subject) = (EmailKinds.OrderCancelled, settings.OrderCancelledSubject);
                    body.Append("<p>Your order <strong>").Append(number).Append("</strong> from ").Append(shop).Append(" was cancelled.</p>");
                    if (!string.IsNullOrWhiteSpace(shopOrder.CancelReason)) body.Append("<p>Reason: ").Append(Html(shopOrder.CancelReason)).Append("</p>");
                    break;
                default:
                    return;
            }

            await EnqueueAsync(shopOrder.Order.CustomerId, kind, subject, shopOrder.Number, body.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, shopOrder.Number);
        }
    }

    private async Task EnqueueAsync(int customerId, string kind, string subjectTemplate, string number, string body, CancellationToken cancellationToken)
    {
        var customer = await customers.FindByIdAsync(customerId, cancellationToken);
        // No customer or a closed account: nobody to write to. Not an error.
        if (customer is null || !customer.Active) return;

        var greeting = string.IsNullOrWhiteSpace(customer.FirstName) ? "Hello," : $"Hello {Html(customer.FirstName)},";
        var result = await queue.EnqueueAsync(kind, new EmailMessage(
            customer.Email, subjectTemplate.Replace("{number}", number, StringComparison.Ordinal), $"<p>{greeting}</p>{body}"), cancellationToken);
        if (!result.Succeeded) LogRefused(number, kind);
    }

    private static void AppendLines(StringBuilder body, List<OrderLine> lines)
    {
        if (lines.Count == 0) return;
        body.Append("<ul>");
        foreach (var line in lines)
        {
            body.Append("<li>").Append(line.Quantity.ToString(CultureInfo.InvariantCulture)).Append(" x ").Append(Html(line.Name));
            if (!string.IsNullOrWhiteSpace(line.VariantLabel)) body.Append(" (").Append(Html(line.VariantLabel)).Append(')');
            body.Append("</li>");
        }
        body.Append("</ul>");
    }

    private static string Recipient(Order order) =>
        string.Join(", ", new[] { order.RecipientName, order.Address1, order.Address2, order.City, order.StateProvince, order.PostalCode, order.CountryCode }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string Money(decimal amount, string currency) => $"{amount.ToString("#,0.##", CultureInfo.InvariantCulture)} {Html(currency)}";

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not queue the email of {Number}.")]
    private partial void LogFailed(Exception exception, string number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The email queue refused the {Kind} email of {Number}.")]
    private partial void LogRefused(string number, string kind);
}
