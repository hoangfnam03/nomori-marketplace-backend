using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Jobs;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ReminderTests
{
    private static readonly DateTime Start = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class ReminderClock : IClock
    {
        public DateTime UtcNow { get; set; } = Start;
    }

    /// <summary>Records like the SQL statements do: one row per (kind, reference), renewed only when older than the given time.</summary>
    private sealed class FakeReminderStore : IReminderStore
    {
        public List<UnpaidOrderCandidate> Orders { get; } = [];
        public List<AbandonedCartCandidate> Carts { get; } = [];
        public Dictionary<(string Kind, int Ref), DateTime> Log { get; } = [];

        public Task<IReadOnlyList<UnpaidOrderCandidate>> UnpaidOrdersAsync(DateTime createdBeforeUtc, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UnpaidOrderCandidate>>(Orders.Where(o => !Log.ContainsKey((ReminderKinds.UnpaidOrder, o.OrderId))).Take(take).ToList());

        public Task<IReadOnlyList<AbandonedCartCandidate>> AbandonedCartsAsync(DateTime idleBeforeUtc, DateTime idleAfterUtc, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AbandonedCartCandidate>>(Carts
                .Where(c => c.LastTouchedUtc < idleBeforeUtc && c.LastTouchedUtc > idleAfterUtc)
                .Where(c => !Log.TryGetValue((ReminderKinds.AbandonedCart, c.CustomerId), out var sent) || sent < c.LastTouchedUtc)
                .Take(take).ToList());

        public Task<bool> TryRecordAsync(string kind, int referenceId, int customerId, DateTime nowUtc, DateTime? renewIfBeforeUtc, CancellationToken cancellationToken)
        {
            if (Log.TryGetValue((kind, referenceId), out var sent) && (renewIfBeforeUtc is null || sent >= renewIfBeforeUtc))
                return Task.FromResult(false);
            Log[(kind, referenceId)] = nowUtc;
            return Task.FromResult(true);
        }

        public Task ForgetAsync(string kind, int referenceId, CancellationToken cancellationToken)
        {
            Log.Remove((kind, referenceId));
            return Task.CompletedTask;
        }

        public Task<int> DeleteOlderThanAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken)
        {
            var old = Log.Where(x => x.Value < beforeUtc).Select(x => x.Key).ToList();
            foreach (var key in old) Log.Remove(key);
            return Task.FromResult(old.Count);
        }
    }

    private sealed class RecordingQueue : IEmailQueueService
    {
        public List<(string Kind, EmailMessage Message)> Queued { get; } = [];
        public bool Refuse { get; set; }
        public bool Throw { get; set; }

        public Task<CatalogResult<long>> EnqueueAsync(string kind, EmailMessage message, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("queue down");
            if (Refuse) return Task.FromResult(CatalogResult.Failure<long>("toAddress", "bad"));
            Queued.Add((kind, message));
            return Task.FromResult(CatalogResult.Success((long)Queued.Count));
        }

        public Task<PagedResult<QueuedEmail>> ListAsync(EmailQueueQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> GetCountsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<QueuedEmail>> GetAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<QueuedEmail>> RetryAsync(long id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<bool>> DeleteAsync(long id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public FakeReminderStore Store { get; } = new();
        public RecordingQueue Queue { get; } = new();
        public ReminderClock Clock { get; } = new();
        public EmailOptions Email { get; set; } = new() { FrontendBaseUrl = "https://shop.test/" };
        public ReminderOptions Reminders { get; set; } = new();
        public FakeCustomerIdentityStore Customers { get; } = new(
            new Customer { Id = 100, Email = "ann@example.com", FirstName = "Ann <b>", Active = true },
            new Customer { Id = 101, Email = "gone@example.com", Active = false });

        public RemindUnpaidOrdersJob Unpaid() => new(
            Store, Customers, Queue, Clock, Options.Create(Reminders), Options.Create(Email), NullLogger<RemindUnpaidOrdersJob>.Instance);

        public RemindAbandonedCartsJob Carts() => new(
            Store, Customers, Queue, Clock, Options.Create(Reminders), Options.Create(Email), NullLogger<RemindAbandonedCartsJob>.Instance);

        public PurgeReminderLogJob Purge() => new(Store, Clock, Options.Create(Reminders));
    }

    private static UnpaidOrderCandidate Order(int id = 1, int customer = 100) => new(id, "NM-" + id, customer, 1234.5m, "USD");

    // ---- Unpaid orders ----

    [Fact]
    public async Task AnUnpaidOrderGetsOneReminderWithItsNumberAndTotalAndEveryValueEncoded()
    {
        var f = new Fixture();
        f.Store.Orders.Add(Order());

        var result = await f.Unpaid().RunAsync(CancellationToken.None);

        Assert.Equal((1, 0), (result.Processed, result.Failed));
        var (kind, message) = Assert.Single(f.Queue.Queued);
        Assert.Equal((ReminderKinds.UnpaidOrder, "ann@example.com", "Complete your payment for order NM-1"), (kind, message.ToAddress, message.Subject));
        Assert.Contains("Hello Ann &lt;b&gt;,", message.HtmlBody);
        Assert.Contains("1,234.5 USD", message.HtmlBody);
        Assert.Contains("href=\"https://shop.test/customer/orders\"", message.HtmlBody);
    }

    [Fact]
    public async Task TheSameOrderIsNeverRemindedTwice()
    {
        var f = new Fixture();
        f.Store.Orders.Add(Order());

        await f.Unpaid().RunAsync(CancellationToken.None);
        await f.Unpaid().RunAsync(CancellationToken.None);

        Assert.Single(f.Queue.Queued);
    }

    [Fact]
    public async Task WithoutAFrontendAddressTheReminderHasNoLink()
    {
        var f = new Fixture { Email = new EmailOptions() };
        f.Store.Orders.Add(Order());

        await f.Unpaid().RunAsync(CancellationToken.None);

        Assert.DoesNotContain("<a ", f.Queue.Queued.Single().Message.HtmlBody);
    }

    [Fact]
    public async Task AMissingOrInactiveCustomerGetsNothingAndIsNotAnError()
    {
        var f = new Fixture();
        f.Store.Orders.Add(Order(1, 101));
        f.Store.Orders.Add(Order(2, 999));

        var result = await f.Unpaid().RunAsync(CancellationToken.None);

        Assert.Equal((0, 0), (result.Processed, result.Failed));
        Assert.Empty(f.Queue.Queued);
    }

    [Fact]
    public async Task AFailureWhileQueuingIsCountedTakesTheRecordBackAndTheNextRunTriesAgain()
    {
        var f = new Fixture();
        f.Store.Orders.Add(Order(1));
        f.Store.Orders.Add(Order(2));
        f.Queue.Throw = true;

        var first = await f.Unpaid().RunAsync(CancellationToken.None);

        Assert.Equal((0, 2), (first.Processed, first.Failed));
        Assert.Empty(f.Store.Log);

        f.Queue.Throw = false;
        var second = await f.Unpaid().RunAsync(CancellationToken.None);

        Assert.Equal((2, 0), (second.Processed, second.Failed));
    }

    [Fact]
    public async Task ARefusedEmailIsNotRetriedEveryRun()
    {
        var f = new Fixture();
        f.Store.Orders.Add(Order());
        f.Queue.Refuse = true;

        await f.Unpaid().RunAsync(CancellationToken.None);
        f.Queue.Refuse = false;
        await f.Unpaid().RunAsync(CancellationToken.None);

        Assert.Empty(f.Queue.Queued);
    }

    [Fact]
    public async Task NothingToRemindIsACleanRun()
    {
        var result = await new Fixture().Unpaid().RunAsync(CancellationToken.None);

        Assert.Equal((0, 0, null), (result.Processed, result.Failed, result.Message));
    }

    // ---- Abandoned carts ----

    [Fact]
    public async Task ACartLeftForADayButLessThanAWeekGetsOneReminder()
    {
        var f = new Fixture();
        f.Store.Carts.Add(new AbandonedCartCandidate(100, 3, Start.AddHours(-30)));
        f.Store.Carts.Add(new AbandonedCartCandidate(101, 1, Start.AddHours(-2)));
        f.Store.Carts.Add(new AbandonedCartCandidate(102, 1, Start.AddDays(-8)));

        var result = await f.Carts().RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        var (kind, message) = Assert.Single(f.Queue.Queued);
        Assert.Equal((ReminderKinds.AbandonedCart, "ann@example.com"), (kind, message.ToAddress));
        Assert.Contains("3 items", message.HtmlBody);
        Assert.Contains("https://shop.test/cart", message.HtmlBody);
    }

    [Fact]
    public async Task OneItemIsWrittenInTheSingular()
    {
        var f = new Fixture();
        f.Store.Carts.Add(new AbandonedCartCandidate(100, 1, Start.AddHours(-30)));

        await f.Carts().RunAsync(CancellationToken.None);

        Assert.Contains("1 item ", f.Queue.Queued.Single().Message.HtmlBody);
    }

    [Fact]
    public async Task ACartIsRemindedOnceUntilTheCustomerChangesItAgain()
    {
        var f = new Fixture();
        f.Store.Carts.Add(new AbandonedCartCandidate(100, 2, Start.AddHours(-30)));

        await f.Carts().RunAsync(CancellationToken.None);
        await f.Carts().RunAsync(CancellationToken.None);
        Assert.Single(f.Queue.Queued);

        // The customer comes back, changes the cart and leaves again.
        f.Clock.UtcNow = Start.AddDays(2);
        f.Store.Carts.Clear();
        f.Store.Carts.Add(new AbandonedCartCandidate(100, 2, Start.AddDays(2).AddHours(-30)));
        await f.Carts().RunAsync(CancellationToken.None);

        Assert.Equal(2, f.Queue.Queued.Count);
    }

    // ---- Purge ----

    [Fact]
    public async Task ThePurgeDeletesOnlyOldRecords()
    {
        var f = new Fixture();
        f.Store.Log[(ReminderKinds.UnpaidOrder, 1)] = Start.AddDays(-91);
        f.Store.Log[(ReminderKinds.UnpaidOrder, 2)] = Start.AddDays(-5);

        var result = await f.Purge().RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Single(f.Store.Log);
    }
}
