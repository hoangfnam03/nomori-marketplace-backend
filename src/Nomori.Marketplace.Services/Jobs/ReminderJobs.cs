using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Jobs;

/// <summary>
/// Reminds customers of an order that still waits for its payment, once per order, before <c>orders.expire_unpaid</c> cancels it. The reminder
/// is recorded first (one statement), so two nodes never send it twice; a failure while queuing takes the record back and the next run tries again.
/// </summary>
public sealed partial class RemindUnpaidOrdersJob(
    IReminderStore reminders,
    ICustomerIdentityStore customers,
    IEmailQueueService queue,
    IClock clock,
    IOptions<ReminderOptions> options,
    IOptions<EmailOptions> email,
    ILogger<RemindUnpaidOrdersJob> logger) : IScheduledJob
{
    public string Name => "reminders.unpaid_orders";
    public string Description => "Emails customers once about an order that still awaits its payment.";
    public int DefaultIntervalMinutes => 10;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var candidates = await reminders.UnpaidOrdersAsync(now.AddMinutes(-Math.Max(1, options.Value.UnpaidOrderMinutes)), JobRules.BatchSize, cancellationToken);
        var sent = 0;
        var failed = 0;
        foreach (var order in candidates)
        {
            try
            {
                if (!await reminders.TryRecordAsync(ReminderKinds.UnpaidOrder, order.OrderId, order.CustomerId, now, null, cancellationToken)) continue;
                if (await SendAsync(order, cancellationToken)) sent++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(exception, order.OrderId);
                failed++;
                await reminders.ForgetAsync(ReminderKinds.UnpaidOrder, order.OrderId, CancellationToken.None);
            }
        }
        return new JobResult(sent, failed, candidates.Count == 0 ? null : $"Reminded {sent} of {candidates.Count} customers.");
    }

    private async Task<bool> SendAsync(UnpaidOrderCandidate order, CancellationToken cancellationToken)
    {
        var customer = await customers.FindByIdAsync(order.CustomerId, cancellationToken);
        // Nobody to write to: not an error, and not worth trying again.
        if (customer is null || !customer.Active) return false;

        var link = ReminderText.Link(email.Value, "/customer/orders");
        var body = $"<p>{ReminderText.Greeting(customer.FirstName)}</p>"
            + $"<p>Your order <strong>{ReminderText.Html(order.Number)}</strong> ({ReminderText.Money(order.Total, order.CurrencyCode)}) is still waiting for its payment. "
            + "If you do not complete it soon, the order is cancelled and the items go back on sale.</p>"
            + (link is null ? string.Empty : $"<p><a href=\"{ReminderText.Html(link)}\">Go to my orders</a></p>");
        var result = await queue.EnqueueAsync(EmailKinds.UnpaidOrderReminder, new EmailMessage(
            customer.Email, $"Complete your payment for order {order.Number}", body), cancellationToken);
        return result.Succeeded;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remind about unpaid order {OrderId}.")]
    private partial void LogFailed(Exception exception, int orderId);
}

/// <summary>
/// Reminds customers of a cart they left alone, once for each time the cart was last changed: a customer who comes back, changes the cart and
/// leaves again can be reminded again; one who does not is reminded once and never again for that cart.
/// </summary>
public sealed partial class RemindAbandonedCartsJob(
    IReminderStore reminders,
    ICustomerIdentityStore customers,
    IEmailQueueService queue,
    IClock clock,
    IOptions<ReminderOptions> options,
    IOptions<EmailOptions> email,
    ILogger<RemindAbandonedCartsJob> logger) : IScheduledJob
{
    public string Name => "reminders.abandoned_carts";
    public string Description => "Emails customers once about a cart they left alone.";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var settings = options.Value;
        var candidates = await reminders.AbandonedCartsAsync(
            now.AddHours(-Math.Max(1, settings.AbandonedCartHours)), now.AddDays(-Math.Max(1, settings.AbandonedCartMaxDays)), JobRules.BatchSize, cancellationToken);
        var sent = 0;
        var failed = 0;
        foreach (var cart in candidates)
        {
            try
            {
                // Renewed only when the cart changed after the last reminder.
                if (!await reminders.TryRecordAsync(ReminderKinds.AbandonedCart, cart.CustomerId, cart.CustomerId, now, cart.LastTouchedUtc, cancellationToken)) continue;
                if (await SendAsync(cart, cancellationToken)) sent++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(exception, cart.CustomerId);
                failed++;
                await reminders.ForgetAsync(ReminderKinds.AbandonedCart, cart.CustomerId, CancellationToken.None);
            }
        }
        return new JobResult(sent, failed, candidates.Count == 0 ? null : $"Reminded {sent} of {candidates.Count} customers.");
    }

    private async Task<bool> SendAsync(AbandonedCartCandidate cart, CancellationToken cancellationToken)
    {
        var customer = await customers.FindByIdAsync(cart.CustomerId, cancellationToken);
        if (customer is null || !customer.Active) return false;

        var link = ReminderText.Link(email.Value, "/cart");
        var count = cart.ItemCount.ToString(CultureInfo.InvariantCulture);
        var body = $"<p>{ReminderText.Greeting(customer.FirstName)}</p>"
            + $"<p>You left {count} {(cart.ItemCount == 1 ? "item" : "items")} in your cart. They are still waiting for you; stock and prices can change, so check out soon.</p>"
            + (link is null ? string.Empty : $"<p><a href=\"{ReminderText.Html(link)}\">Go to my cart</a></p>");
        var result = await queue.EnqueueAsync(EmailKinds.AbandonedCartReminder, new EmailMessage(
            customer.Email, "You left something in your cart", body), cancellationToken);
        return result.Succeeded;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remind customer {CustomerId} about the cart.")]
    private partial void LogFailed(Exception exception, int customerId);
}

/// <summary>Deletes old reminder records, so the table does not grow for ever.</summary>
public sealed class PurgeReminderLogJob(IReminderStore reminders, IClock clock, IOptions<ReminderOptions> options) : IScheduledJob
{
    public string Name => "reminders.purge_log";
    public string Description => "Deletes reminder records older than the retention period.";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var deleted = await reminders.DeleteOlderThanAsync(
            clock.UtcNow.AddDays(-Math.Max(1, options.Value.LogRetentionDays)), JobRules.BatchSize * 5, cancellationToken);
        return new JobResult(deleted, 0, deleted == 0 ? null : $"Deleted {deleted} old reminder records.");
    }
}

internal static class ReminderText
{
    public static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    public static string Greeting(string? firstName) => string.IsNullOrWhiteSpace(firstName) ? "Hello," : $"Hello {Html(firstName)},";

    public static string Money(decimal amount, string currency) => $"{amount.ToString("#,0.##", CultureInfo.InvariantCulture)} {Html(currency)}";

    /// <summary>A plain page of the storefront (no token, nothing secret). Null while the base address is not configured.</summary>
    public static string? Link(EmailOptions options, string path) =>
        string.IsNullOrWhiteSpace(options.FrontendBaseUrl) ? null : options.FrontendBaseUrl.TrimEnd('/') + path;
}
