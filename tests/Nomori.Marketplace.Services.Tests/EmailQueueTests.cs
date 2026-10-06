using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Email;
using Nomori.Marketplace.Services.Jobs;
using Nomori.Marketplace.Services.Orders;

namespace Nomori.Marketplace.Services.Tests;

/// <summary>Records what the order service announces, instead of queuing emails.</summary>
internal sealed class RecordingOrderNotifier : IOrderNotifier
{
    public List<string> Placed { get; } = [];
    public List<(string Number, ShopOrderStatus Status)> Changed { get; } = [];

    public Task OrderPlacedAsync(Order order, CancellationToken cancellationToken)
    {
        Placed.Add(order.Number);
        return Task.CompletedTask;
    }

    public Task ShopOrderChangedAsync(ShopOrder shopOrder, CancellationToken cancellationToken)
    {
        Changed.Add((shopOrder.Number, shopOrder.Status));
        return Task.CompletedTask;
    }
}

public sealed class EmailQueueTests
{
    private static readonly DateTime Start = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class QueueClock : IClock
    {
        public DateTime UtcNow { get; set; } = Start;
    }

    /// <summary>The claim works as the SQL statement does: the same rules, in memory.</summary>
    private sealed class FakeQueueStore : IEmailQueueStore
    {
        public List<QueuedEmail> Emails { get; } = [];
        private long nextId = 1;

        public Task<long> InsertAsync(QueuedEmail email, CancellationToken cancellationToken)
        {
            email.Id = nextId++;
            Emails.Add(email);
            return Task.FromResult(email.Id);
        }

        public Task<IReadOnlyList<QueuedEmail>> ClaimDueAsync(DateTime nowUtc, DateTime leaseUntilUtc, int take, CancellationToken cancellationToken)
        {
            var due = Emails
                .Where(e => (e.Status == QueuedEmailStatuses.Pending && e.NextAttemptUtc <= nowUtc)
                    || (e.Status == QueuedEmailStatuses.Sending && e.LockedUntilUtc <= nowUtc))
                .OrderBy(e => e.NextAttemptUtc).ThenBy(e => e.Id).Take(take).ToList();
            foreach (var e in due)
                (e.Status, e.LockedUntilUtc, e.Attempts) = (QueuedEmailStatuses.Sending, leaseUntilUtc, e.Attempts + 1);
            return Task.FromResult<IReadOnlyList<QueuedEmail>>(due);
        }

        public Task MarkSentAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var e = Emails.Single(x => x.Id == id);
            (e.Status, e.SentOnUtc, e.LockedUntilUtc, e.LastError) = (QueuedEmailStatuses.Sent, nowUtc, null, null);
            return Task.CompletedTask;
        }

        public Task MarkAttemptFailedAsync(long id, string failure, DateTime? nextAttemptUtc, CancellationToken cancellationToken)
        {
            var e = Emails.Single(x => x.Id == id);
            e.Status = nextAttemptUtc is null ? QueuedEmailStatuses.Failed : QueuedEmailStatuses.Pending;
            if (nextAttemptUtc is not null) e.NextAttemptUtc = nextAttemptUtc.Value;
            (e.LockedUntilUtc, e.LastError) = (null, failure);
            return Task.CompletedTask;
        }

        public Task<bool> RetryAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var e = Emails.SingleOrDefault(x => x.Id == id && x.Status == QueuedEmailStatuses.Failed);
            if (e is null) return Task.FromResult(false);
            (e.Status, e.Attempts, e.NextAttemptUtc) = (QueuedEmailStatuses.Pending, 0, nowUtc);
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var e = Emails.SingleOrDefault(x => x.Id == id && (x.Status != QueuedEmailStatuses.Sending || x.LockedUntilUtc <= nowUtc));
            return Task.FromResult(e is not null && Emails.Remove(e));
        }

        public Task<int> DeleteOldAsync(DateTime sentBeforeUtc, DateTime failedBeforeUtc, int take, CancellationToken cancellationToken) =>
            Task.FromResult(Emails.RemoveAll(e =>
                (e.Status == QueuedEmailStatuses.Sent && e.SentOnUtc < sentBeforeUtc)
                || (e.Status == QueuedEmailStatuses.Failed && e.CreatedOnUtc < failedBeforeUtc)));

        public Task<PagedResult<QueuedEmail>> ListAsync(EmailQueueQuery query, CancellationToken cancellationToken)
        {
            var rows = Emails.Where(e => query.Status is null || e.Status == query.Status)
                .Where(e => query.Search is null || e.ToAddress.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(new PagedResult<QueuedEmail>(
                rows.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), rows.Count, query.Page, query.PageSize));
        }

        public Task<QueuedEmail?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Emails.SingleOrDefault(e => e.Id == id));

        public Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(QueuedEmailStatuses.All.ToDictionary(s => s, s => Emails.Count(e => e.Status == s)));
    }

    private sealed class FakeSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Func<EmailMessage, Exception?> Fail { get; set; } = _ => null;

        public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (Fail(message) is { } failure) throw failure;
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public FakeQueueStore Store { get; } = new();
        public FakeSender Sender { get; } = new();
        public QueueClock Clock { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public EmailOptions Options { get; set; } = new() { Enabled = true };

        public EmailQueueService Queue() => new(Store, Audit, Clock);

        public SendQueuedEmailsJob SendJob() => new(Store, Sender, Clock, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<SendQueuedEmailsJob>.Instance);

        public PurgeEmailQueueJob PurgeJob() => new(Store, Clock, Microsoft.Extensions.Options.Options.Create(Options));

        public async Task<long> QueueAsync(string to = "ann@example.com", string kind = EmailKinds.OrderPlaced) =>
            (await Queue().EnqueueAsync(kind, new EmailMessage(to, "Hello", "<p>Hi</p>"), CancellationToken.None)).Value;
    }

    // ---- Rules ----

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 10)]
    [InlineData(3, 30)]
    [InlineData(4, 120)]
    [InlineData(9, 120)]
    [InlineData(0, 2)]
    public void TheWaitGrowsWithEveryFailureAndStopsGrowing(int attempts, int minutes) =>
        Assert.Equal(Start.AddMinutes(minutes), EmailQueueRules.NextAttempt(Start, attempts));

    [Theory]
    [InlineData("ann@example.com", true)]
    [InlineData("  ann@example.com ", true)]
    [InlineData("Ann <ann@example.com>", false)]
    [InlineData("not-an-address", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyABareAddressIsAccepted(string? address, bool valid) => Assert.Equal(valid, EmailQueueRules.IsValidAddress(address));

    [Fact]
    public void AFailureIsOneShortLineWithoutAStackTrace()
    {
        var text = EmailQueueRules.DescribeFailure(new InvalidOperationException("line one\nline two " + new string('x', 800)));

        Assert.StartsWith("InvalidOperationException: line one line two", text);
        Assert.DoesNotContain('\n', text);
        Assert.Equal(EmailQueueRules.MaxErrorLength, text.Length);
    }

    // ---- Queue service ----

    [Fact]
    public async Task AnEmailIsQueuedAsPendingAndDueNow()
    {
        var f = new Fixture();

        var id = await f.QueueAsync("  ann@example.com ");

        var email = Assert.Single(f.Store.Emails);
        Assert.Equal((id, "ann@example.com", QueuedEmailStatuses.Pending, 0, Start), (email.Id, email.ToAddress, email.Status, email.Attempts, email.NextAttemptUtc));
    }

    [Fact]
    public async Task ABadAddressAnEmptySubjectOrAnEmptyBodyIsRefusedAndNothingIsQueued()
    {
        var f = new Fixture();

        Assert.Contains("toAddress", (await f.Queue().EnqueueAsync("k", new EmailMessage("nope", "S", "<p>b</p>"), CancellationToken.None)).Errors.Keys);
        Assert.Contains("message", (await f.Queue().EnqueueAsync("k", new EmailMessage("a@b.co", " ", "<p>b</p>"), CancellationToken.None)).Errors.Keys);
        Assert.Contains("message", (await f.Queue().EnqueueAsync("k", new EmailMessage("a@b.co", "S", " "), CancellationToken.None)).Errors.Keys);
        Assert.Contains("kind", (await f.Queue().EnqueueAsync(" ", new EmailMessage("a@b.co", "S", "<p>b</p>"), CancellationToken.None)).Errors.Keys);

        Assert.Empty(f.Store.Emails);
    }

    [Fact]
    public async Task ALongSubjectIsCutAndLineBreaksInItAreRemoved()
    {
        var f = new Fixture();

        await f.Queue().EnqueueAsync("k", new EmailMessage("a@b.co", "Hi\r\nthere " + new string('x', 400), "<p>b</p>"), CancellationToken.None);

        var subject = f.Store.Emails.Single().Subject;
        Assert.Equal(EmailQueueRules.MaxSubjectLength, subject.Length);
        Assert.DoesNotContain('\n', subject);
    }

    [Fact]
    public async Task ListingClampsThePageAndIgnoresAnUnknownStatus()
    {
        var f = new Fixture();
        await f.QueueAsync();

        var page = await f.Queue().ListAsync(new EmailQueueQuery("bogus", " ", 0, 5000), CancellationToken.None);

        Assert.Equal((1, EmailQueueRules.MaxPageSize, 1), (page.Page, page.PageSize, page.Items.Count));
    }

    [Fact]
    public async Task OnlyAFailedEmailCanBeRetriedAndTheRetryIsAudited()
    {
        var f = new Fixture();
        var id = await f.QueueAsync();

        Assert.Equal(EmailQueueErrors.NotRetryable, (await f.Queue().RetryAsync(id, 1, CancellationToken.None)).ErrorCode);

        f.Store.Emails.Single().Status = QueuedEmailStatuses.Failed;
        f.Store.Emails.Single().Attempts = 5;
        var retried = await f.Queue().RetryAsync(id, 1, CancellationToken.None);

        Assert.Equal((QueuedEmailStatuses.Pending, 0), (retried.Value!.Status, retried.Value.Attempts));
        Assert.Contains("email.retry", f.Audit.Entries.Select(e => e.Event));
        Assert.Equal(CatalogErrors.NotFound, (await f.Queue().RetryAsync(999, 1, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task AnEmailBeingSentCannotBeDeletedButAnyOtherCanAndTheDeleteIsAudited()
    {
        var f = new Fixture();
        var busy = await f.QueueAsync();
        var other = await f.QueueAsync();
        f.Store.Emails.Single(e => e.Id == busy).Status = QueuedEmailStatuses.Sending;
        f.Store.Emails.Single(e => e.Id == busy).LockedUntilUtc = Start.AddMinutes(5);

        Assert.Equal(EmailQueueErrors.Busy, (await f.Queue().DeleteAsync(busy, 1, CancellationToken.None)).ErrorCode);
        Assert.True((await f.Queue().DeleteAsync(other, 1, CancellationToken.None)).Succeeded);

        Assert.Single(f.Store.Emails);
        Assert.Contains("email.deleted", f.Audit.Entries.Select(e => e.Event));
        Assert.Equal(CatalogErrors.NotFound, (await f.Queue().DeleteAsync(999, 1, CancellationToken.None)).ErrorCode);
    }

    // ---- Sending job ----

    [Fact]
    public async Task TheJobSendsDueEmailsAndMarksThemSent()
    {
        var f = new Fixture();
        await f.QueueAsync("a@example.com");
        await f.QueueAsync("b@example.com");

        var result = await f.SendJob().RunAsync(CancellationToken.None);

        Assert.Equal((2, 0), (result.Processed, result.Failed));
        Assert.Equal(["a@example.com", "b@example.com"], f.Sender.Sent.Select(m => m.ToAddress));
        Assert.All(f.Store.Emails, e => Assert.Equal((QueuedEmailStatuses.Sent, 1, Start), (e.Status, e.Attempts, e.SentOnUtc)));
    }

    [Fact]
    public async Task AnEmailQueuedForLaterWaitsForItsTime()
    {
        var f = new Fixture();
        await f.QueueAsync();
        f.Store.Emails.Single().NextAttemptUtc = Start.AddMinutes(10);

        Assert.Equal(0, (await f.SendJob().RunAsync(CancellationToken.None)).Processed);
        f.Clock.UtcNow = Start.AddMinutes(10);
        Assert.Equal(1, (await f.SendJob().RunAsync(CancellationToken.None)).Processed);
    }

    [Fact]
    public async Task AFailedSendGoesBackToPendingWithABackoffAndTheNextEmailStillGoes()
    {
        var f = new Fixture();
        await f.QueueAsync("bad@example.com");
        await f.QueueAsync("good@example.com");
        f.Sender.Fail = m => m.ToAddress == "bad@example.com" ? new InvalidOperationException("mailbox full") : null;

        var result = await f.SendJob().RunAsync(CancellationToken.None);

        Assert.Equal((1, 1), (result.Processed, result.Failed));
        var bad = f.Store.Emails.Single(e => e.ToAddress == "bad@example.com");
        Assert.Equal((QueuedEmailStatuses.Pending, 1, Start.AddMinutes(2)), (bad.Status, bad.Attempts, bad.NextAttemptUtc));
        Assert.Equal("InvalidOperationException: mailbox full", bad.LastError);
        Assert.Equal(QueuedEmailStatuses.Sent, f.Store.Emails.Single(e => e.ToAddress == "good@example.com").Status);
    }

    [Fact]
    public async Task AfterTheLastAttemptTheEmailIsFailedForGoodUntilAnAdministratorRetriesIt()
    {
        var f = new Fixture { Options = new EmailOptions { Enabled = true, QueueMaxAttempts = 3 } };
        var id = await f.QueueAsync();
        f.Sender.Fail = _ => new InvalidOperationException("down");

        for (var i = 0; i < 3; i++)
        {
            await f.SendJob().RunAsync(CancellationToken.None);
            f.Clock.UtcNow = f.Clock.UtcNow.AddHours(3);
        }

        var email = f.Store.Emails.Single();
        Assert.Equal((QueuedEmailStatuses.Failed, 3), (email.Status, email.Attempts));
        Assert.Equal(0, (await f.SendJob().RunAsync(CancellationToken.None)).Processed);

        f.Sender.Fail = _ => null;
        await f.Queue().RetryAsync(id, 1, CancellationToken.None);
        Assert.Equal(1, (await f.SendJob().RunAsync(CancellationToken.None)).Processed);
        Assert.Equal(QueuedEmailStatuses.Sent, email.Status);
    }

    [Fact]
    public async Task ASendingEmailWhoseLeaseEndedIsTakenAgainAndOneWithALiveLeaseIsNot()
    {
        var f = new Fixture();
        await f.QueueAsync();
        var email = f.Store.Emails.Single();
        (email.Status, email.LockedUntilUtc, email.Attempts) = (QueuedEmailStatuses.Sending, Start.AddMinutes(5), 1);

        Assert.Equal(0, (await f.SendJob().RunAsync(CancellationToken.None)).Processed);

        f.Clock.UtcNow = Start.AddMinutes(6);
        Assert.Equal(1, (await f.SendJob().RunAsync(CancellationToken.None)).Processed);
        Assert.Equal(2, email.Attempts);
    }

    [Fact]
    public async Task WhileDeliveryIsOffNothingIsTakenAndTheMailWaits()
    {
        var f = new Fixture { Options = new EmailOptions { Enabled = false } };
        await f.QueueAsync();

        var result = await f.SendJob().RunAsync(CancellationToken.None);

        Assert.Equal((0, 0), (result.Processed, result.Failed));
        Assert.Empty(f.Sender.Sent);
        Assert.Equal((QueuedEmailStatuses.Pending, 0), (f.Store.Emails.Single().Status, f.Store.Emails.Single().Attempts));
    }

    [Fact]
    public async Task AnEmptyQueueIsACleanRun()
    {
        var f = new Fixture();

        var result = await f.SendJob().RunAsync(CancellationToken.None);

        Assert.Equal((0, 0, null), (result.Processed, result.Failed, result.Message));
    }

    // ---- Purge job ----

    [Fact]
    public async Task ThePurgeDeletesOldSentAndOldFailedEmailsOnly()
    {
        var f = new Fixture { Options = new EmailOptions { SentRetentionDays = 30, FailedRetentionDays = 90 } };
        QueuedEmail Add(string status, int daysOld) => new()
        {
            Kind = "k", ToAddress = "a@b.co", Subject = "s", HtmlBody = "b", Status = status,
            CreatedOnUtc = Start.AddDays(-daysOld), SentOnUtc = status == QueuedEmailStatuses.Sent ? Start.AddDays(-daysOld) : null
        };
        foreach (var e in new[]
        {
            Add(QueuedEmailStatuses.Sent, 31), Add(QueuedEmailStatuses.Sent, 5), Add(QueuedEmailStatuses.Failed, 91),
            Add(QueuedEmailStatuses.Failed, 40), Add(QueuedEmailStatuses.Pending, 400)
        })
            await f.Store.InsertAsync(e, CancellationToken.None);

        var result = await f.PurgeJob().RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(3, f.Store.Emails.Count);
        Assert.Contains(f.Store.Emails, e => e.Status == QueuedEmailStatuses.Pending);
    }

    // ---- Order notifier ----

    private sealed class NotifierFixture
    {
        public FakeQueueStore Store { get; } = new();
        public FakeCustomerIdentityStore Customers { get; } = new(
            new Customer { Id = 100, Email = "ann@example.com", FirstName = "Ann <b>", Active = true },
            new Customer { Id = 101, Email = "gone@example.com", Active = false });
        public EmailOptions Options { get; set; } = new();

        public OrderNotifier Create() => new(
            Customers, new EmailQueueService(Store, new RecordingAuditLog(), new QueueClock()),
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<OrderNotifier>.Instance);
    }

    private static Order SampleOrder(int customer = 100) => new()
    {
        Id = 1, Number = "NM-1001", CustomerId = customer, CurrencyCode = "USD", Total = 1234.5m, PaymentMethod = "cod",
        RecipientName = "Ann Lee", Address1 = "1 Main St", City = "Springfield", CountryCode = "US",
        ShopOrders =
        [
            new ShopOrder
            {
                Number = "NM-1001-1", ShopName = "Mugs & Co", ShippingMethodName = "Standard", ShippingFee = 5m, Total = 1234.5m,
                Lines = [new OrderLine { Name = "<script>Mug</script>", VariantLabel = "Red", Quantity = 2 }]
            }
        ]
    };

    [Fact]
    public async Task TheOrderEmailGoesToTheCustomerWithTheNumberInTheSubjectAndEveryValueEncoded()
    {
        var f = new NotifierFixture();

        await f.Create().OrderPlacedAsync(SampleOrder(), CancellationToken.None);

        var email = Assert.Single(f.Store.Emails);
        Assert.Equal(("ann@example.com", EmailKinds.OrderPlaced, "Your Nomori Marketplace order NM-1001"), (email.ToAddress, email.Kind, email.Subject));
        Assert.Contains("Hello Ann &lt;b&gt;,", email.HtmlBody);
        Assert.Contains("Mugs &amp; Co", email.HtmlBody);
        Assert.Contains("&lt;script&gt;Mug&lt;/script&gt;", email.HtmlBody);
        Assert.DoesNotContain("<script>", email.HtmlBody);
        Assert.Contains("1,234.5 USD", email.HtmlBody);
    }

    [Theory]
    [InlineData(ShopOrderStatus.Shipped, EmailKinds.OrderShipped, "DHL")]
    [InlineData(ShopOrderStatus.Delivered, EmailKinds.OrderDelivered, "was delivered")]
    [InlineData(ShopOrderStatus.Cancelled, EmailKinds.OrderCancelled, "Out of stock")]
    public async Task AShippedDeliveredOrCancelledShopOrderIsAnnounced(ShopOrderStatus status, string kind, string expected)
    {
        var f = new NotifierFixture();
        var order = SampleOrder();
        var shop = order.ShopOrders[0];
        (shop.Status, shop.Carrier, shop.TrackingNumber, shop.CancelReason, shop.Order) = (status, "DHL", "T-1", "Out of stock", order);

        await f.Create().ShopOrderChangedAsync(shop, CancellationToken.None);

        var email = Assert.Single(f.Store.Emails);
        Assert.Equal(kind, email.Kind);
        Assert.Contains("NM-1001-1", email.Subject);
        Assert.Contains(expected, email.HtmlBody);
    }

    [Theory]
    [InlineData(ShopOrderStatus.Pending)]
    [InlineData(ShopOrderStatus.Confirmed)]
    [InlineData(ShopOrderStatus.Completed)]
    public async Task OtherStatusesSendNothing(ShopOrderStatus status)
    {
        var f = new NotifierFixture();
        var order = SampleOrder();
        order.ShopOrders[0].Status = status;
        order.ShopOrders[0].Order = order;

        await f.Create().ShopOrderChangedAsync(order.ShopOrders[0], CancellationToken.None);

        Assert.Empty(f.Store.Emails);
    }

    [Fact]
    public async Task ACustomerWhoIsGoneOrInactiveGetsNothingAndNothingThrows()
    {
        var f = new NotifierFixture();

        await f.Create().OrderPlacedAsync(SampleOrder(customer: 101), CancellationToken.None);
        await f.Create().OrderPlacedAsync(SampleOrder(customer: 999), CancellationToken.None);

        Assert.Empty(f.Store.Emails);
    }

    [Fact]
    public async Task AFailureWhileQueuingNeverReachesTheCaller()
    {
        var f = new NotifierFixture();
        var notifier = new OrderNotifier(
            f.Customers, new ThrowingQueue(), Microsoft.Extensions.Options.Options.Create(f.Options), NullLogger<OrderNotifier>.Instance);

        await notifier.OrderPlacedAsync(SampleOrder(), CancellationToken.None);
    }

    private sealed class ThrowingQueue : IEmailQueueService
    {
        public Task<CatalogResult<long>> EnqueueAsync(string kind, EmailMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database down");

        public Task<PagedResult<QueuedEmail>> ListAsync(EmailQueueQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> GetCountsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<QueuedEmail>> GetAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<QueuedEmail>> RetryAsync(long id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<bool>> DeleteAsync(long id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
