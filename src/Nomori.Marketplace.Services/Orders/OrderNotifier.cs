using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Orders;

/// <summary>Order emails. A delivery failure is logged and never undoes the order action that triggered it.</summary>
public sealed partial class OrderNotifier(
    IEmailSender emailSender,
    IVendorMemberStore memberStore,
    IOptions<EmailOptions> emailOptions,
    ILogger<OrderNotifier> logger)
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    /// <summary>The customer gets a summary; every member of each shop gets its new shop order.</summary>
    public async Task OrderPlacedAsync(CustomerOrder order, string customerEmail, CancellationToken cancellationToken)
    {
        var options = emailOptions.Value;
        if (!options.Enabled) return;

        await SendAsync(customerEmail, string.Format(CultureInfo.InvariantCulture, options.OrderPlacedSubject, order.OrderNumber),
            CustomerSummary(order, options), order.OrderNumber, cancellationToken);

        foreach (var storeOrder in order.StoreOrders)
        {
            var html = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"<p>Your shop has a new order <strong>{Encoder.Encode(storeOrder.SubOrderNumber)}</strong>.</p>")
                .Append(Lines(storeOrder, order.CurrencyCode))
                .Append(CultureInfo.InvariantCulture, $"<p>Total: {Money(storeOrder.Total, order.CurrencyCode)}. Please confirm it before {storeOrder.ConfirmByUtc:yyyy-MM-dd HH:mm} UTC.</p>")
                .ToString();
            await SendToMembersAsync(storeOrder.VendorId, string.Format(CultureInfo.InvariantCulture, options.StoreOrderNewSubject, storeOrder.SubOrderNumber),
                html, storeOrder.SubOrderNumber, cancellationToken);
        }
    }

    public async Task CancelledByCustomerAsync(StoreOrder storeOrder, string? reason, string? note, CancellationToken cancellationToken)
    {
        var options = emailOptions.Value;
        if (!options.Enabled) return;

        var html = $"<p>The customer cancelled order <strong>{Encoder.Encode(storeOrder.SubOrderNumber)}</strong>. The stock was put back.</p>"
            + $"<p>Reason: {Encoder.Encode(reason ?? string.Empty)}{(string.IsNullOrWhiteSpace(note) ? string.Empty : " — " + Encoder.Encode(note))}</p>";
        await SendToMembersAsync(storeOrder.VendorId, string.Format(CultureInfo.InvariantCulture, options.StoreOrderCancelledByCustomerSubject, storeOrder.SubOrderNumber),
            html, storeOrder.SubOrderNumber, cancellationToken);
    }

    /// <summary>Tells the customer that a shop or an administrator confirmed, shipped or cancelled their shop order.</summary>
    public async Task StatusChangedForCustomerAsync(
        StoreOrder storeOrder, StoreOrderStatus status, string customerEmail, string? reason, string? note,
        string? carrier, string? trackingNumber, CancellationToken cancellationToken)
    {
        var options = emailOptions.Value;
        if (!options.Enabled) return;

        var shop = Encoder.Encode(storeOrder.VendorName ?? "The shop");
        var number = Encoder.Encode(storeOrder.SubOrderNumber);
        var (subject, html) = status switch
        {
            StoreOrderStatus.Confirmed => (options.StoreOrderConfirmedSubject,
                $"<p>{shop} confirmed your order <strong>{number}</strong> and is preparing it.</p>"),
            StoreOrderStatus.Shipped => (options.StoreOrderShippedSubject,
                $"<p>{shop} handed your order <strong>{number}</strong> to {Encoder.Encode(carrier ?? string.Empty)}.</p>"
                + $"<p>Tracking number: <strong>{Encoder.Encode(trackingNumber ?? string.Empty)}</strong></p>"),
            _ => (options.StoreOrderCancelledSubject,
                $"<p>Your order <strong>{number}</strong> from {shop} was cancelled.</p>"
                + $"<p>Reason: {Encoder.Encode(reason ?? string.Empty)}{(string.IsNullOrWhiteSpace(note) ? string.Empty : " — " + Encoder.Encode(note))}</p>"
                + "<p>Nothing will be collected for this order.</p>")
        };

        var baseUrl = options.FrontendBaseUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(baseUrl))
            html += $"<p><a href=\"{Encoder.Encode($"{baseUrl}/customer/orders/{storeOrder.OrderId}")}\">View your order</a></p>";
        await SendAsync(customerEmail, string.Format(CultureInfo.InvariantCulture, subject, storeOrder.SubOrderNumber), html, storeOrder.SubOrderNumber, cancellationToken);
    }

    private static string CustomerSummary(CustomerOrder order, EmailOptions options)
    {
        var html = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"<p>Thank you. Your order <strong>{Encoder.Encode(order.OrderNumber)}</strong> was placed and is waiting for the shops to confirm.</p>");
        foreach (var storeOrder in order.StoreOrders)
        {
            html.Append(CultureInfo.InvariantCulture, $"<h3>{Encoder.Encode(storeOrder.VendorName ?? string.Empty)} ({Encoder.Encode(storeOrder.SubOrderNumber)})</h3>")
                .Append(Lines(storeOrder, order.CurrencyCode));
        }

        var a = order.ShippingAddress;
        html.Append(CultureInfo.InvariantCulture, $"<p>Items: {Money(order.ItemsTotal, order.CurrencyCode)}<br>Shipping: {Money(order.ShippingTotal, order.CurrencyCode)}<br><strong>Total: {Money(order.Total, order.CurrencyCode)}</strong> (cash on delivery)</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>Deliver to: {Encoder.Encode($"{a.FirstName} {a.LastName}, {a.Address1}, {a.City}, {a.CountryCode}")}</p>");

        var baseUrl = options.FrontendBaseUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(baseUrl))
            html.Append(CultureInfo.InvariantCulture, $"<p><a href=\"{Encoder.Encode($"{baseUrl}/customer/orders/{order.Id}")}\">View your order</a></p>");
        return html.ToString();
    }

    private static string Lines(StoreOrder storeOrder, string currency)
    {
        var html = new StringBuilder("<ul>");
        foreach (var item in storeOrder.Items)
        {
            var variant = string.IsNullOrEmpty(item.VariantDescription) ? string.Empty : $" ({Encoder.Encode(item.VariantDescription)})";
            html.Append(CultureInfo.InvariantCulture, $"<li>{Encoder.Encode(item.ProductName)}{variant} × {item.Quantity}: {Money(item.LineTotal, currency)}</li>");
        }
        return html.Append("</ul>").ToString();
    }

    private static string Money(decimal amount, string currency) =>
        $"{amount.ToString("0.####", CultureInfo.InvariantCulture)} {currency}";

    private async Task SendToMembersAsync(int vendorId, string subject, string html, string reference, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var member in await memberStore.ListAsync(vendorId, cancellationToken))
                await emailSender.SendEmailAsync(new EmailMessage(member.Email, subject, html, null), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEmailFailed(ex, reference);
        }
    }

    private async Task SendAsync(string to, string subject, string html, string reference, CancellationToken cancellationToken)
    {
        try
        {
            await emailSender.SendEmailAsync(new EmailMessage(to, subject, html, null), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEmailFailed(ex, reference);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send an email about order {Reference}.")]
    private partial void LogEmailFailed(Exception exception, string reference);
}
