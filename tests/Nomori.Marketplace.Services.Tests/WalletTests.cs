using Microsoft.Extensions.Logging.Abstractions;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Wallets;
using Nomori.Marketplace.Services.Payments;
using Nomori.Marketplace.Services.Wallets;

namespace Nomori.Marketplace.Services.Tests;

/// <summary>Records the shop orders the order service hands over after a cancel.</summary>
internal sealed class RecordingCancellationRefunds : IOrderCancellationRefunds
{
    public List<(int ShopOrderId, OrderActor Actor)> Cancelled { get; } = [];

    public Task RefundCancelledAsync(ShopOrder shopOrder, OrderActor actor, CancellationToken cancellationToken)
    {
        Cancelled.Add((shopOrder.Id, actor));
        return Task.CompletedTask;
    }
}

public sealed class WalletTests
{
    private const int Buyer = 100;
    private const int Other = 200;
    private const string Secret = "unit-test-sandbox-secret";

    /// <summary>The wallets as the database keeps them: one balance per customer, never below zero, each key posted once.</summary>
    private sealed class FakeWalletStore : IWalletStore
    {
        private long nextId = 1;
        public Dictionary<int, decimal> Balances { get; } = [];
        public List<WalletEntry> Entries { get; } = [];
        private readonly HashSet<string> keys = [];

        public Task<decimal> GetBalanceAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Balances.GetValueOrDefault(customerId));

        public Task<PagedResult<WalletEntry>> GetEntriesAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken)
        {
            var mine = Entries.Where(e => e.CustomerId == customerId).OrderByDescending(e => e.Id).ToList();
            return Task.FromResult(new PagedResult<WalletEntry>(mine.Skip((page - 1) * pageSize).Take(pageSize).ToList(), mine.Count, page, pageSize));
        }

        public Task<WalletPostResult> PostAsync(WalletPosting posting, CancellationToken cancellationToken)
        {
            var balance = Balances.GetValueOrDefault(posting.CustomerId);
            if (keys.Contains(posting.IdempotencyKey)) return Task.FromResult(new WalletPostResult(WalletPostOutcome.AlreadyPosted, balance));
            if (balance + posting.Amount < 0) return Task.FromResult(new WalletPostResult(WalletPostOutcome.Insufficient, balance));

            keys.Add(posting.IdempotencyKey);
            balance += posting.Amount;
            Balances[posting.CustomerId] = balance;
            Entries.Add(new WalletEntry(nextId++, posting.CustomerId, posting.Kind, posting.Amount, balance, posting.ReferenceType, posting.ReferenceId,
                posting.Note, posting.NowUtc));
            return Task.FromResult(new WalletPostResult(WalletPostOutcome.Posted, balance));
        }
    }

    private sealed class Fixture
    {
        public FakeWalletStore Wallets { get; } = new();
        public PaymentTests.FakePaymentStore PaymentStore { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture()
        {
            PaymentStore.Methods.Add(new PaymentMethodSetting { SystemName = WalletPaymentProvider.Name, Enabled = true });
            PaymentStore.Methods.Add(new PaymentMethodSetting { SystemName = HostedSandboxPaymentProvider.Name, Enabled = true });
        }

        public WalletPaymentProvider Provider() => new(Wallets, new TestClock());

        public PaymentService Payments() => new(
            PaymentStore,
            [new CashOnDeliveryProvider(), new SandboxPaymentProvider(Secret), new HostedSandboxPaymentProvider(Secret, "http://app.test/"), Provider()],
            [new WalletTopUpHandler(Wallets, new TestClock())], new FakePrimaryCurrency(), Audit, new TestClock());

        public WalletService Service() => new(Wallets, Payments(), new FakePrimaryCurrency(), Audit, new TestClock());

        public Task<CatalogResult<PaymentTransaction>> PayAsync(decimal amount, string key = "order-1", int customer = Buyer) =>
            Payments().CreateAsync(new CreatePaymentCommand(PaymentReferenceTypes.Checkout, 7, key, WalletPaymentProvider.Name, amount, customer),
                CancellationToken.None);

        public Task<WalletPostResult> FundAsync(decimal amount, int customer = Buyer) =>
            Service().CreditAsync(customer, WalletEntryKind.Adjustment, amount, $"fund-{Guid.NewGuid():N}", null, null, null, CancellationToken.None);
    }

    // ---- Paying with the wallet ----

    [Fact]
    public async Task PayingWithTheWalletTakesTheMoneyAtOnceAndThePaymentIsPaid()
    {
        var f = new Fixture();
        await f.FundAsync(100_000m);

        var paid = await f.PayAsync(60_000m);

        Assert.Equal(PaymentStatus.Paid, paid.Value!.Status);
        Assert.Equal(40_000m, f.Wallets.Balances[Buyer]);
        var entry = f.Wallets.Entries.Last();
        Assert.Equal((WalletEntryKind.Payment, -60_000m, 40_000m), (entry.Kind, entry.Amount, entry.BalanceAfter));
        Assert.Null(paid.Value.RedirectUrl);
    }

    [Fact]
    public async Task AShortBalanceRefusesThePaymentAndTakesNothing()
    {
        var f = new Fixture();
        await f.FundAsync(50_000m);

        var refused = await f.PayAsync(60_000m);

        Assert.Equal(PaymentErrors.ProviderFailed, refused.ErrorCode);
        Assert.Equal(50_000m, f.Wallets.Balances[Buyer]);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(f.PaymentStore.Payments).Status);
        Assert.Equal("wallet_insufficient", f.PaymentStore.Payments[0].FailureCode);
    }

    [Fact]
    public async Task TheSamePaymentSentTwiceTakesTheMoneyOnce()
    {
        var f = new Fixture();
        await f.FundAsync(100_000m);

        await f.PayAsync(60_000m);
        var again = await f.PayAsync(60_000m);

        Assert.True(again.Succeeded);
        Assert.Equal(40_000m, f.Wallets.Balances[Buyer]);
    }

    [Fact]
    public async Task OneCustomersMoneyNeverPaysForAnother()
    {
        var f = new Fixture();
        await f.FundAsync(100_000m, Other);

        var refused = await f.PayAsync(10_000m);

        Assert.Equal(PaymentErrors.ProviderFailed, refused.ErrorCode);
        Assert.Equal(100_000m, f.Wallets.Balances[Other]);
    }

    [Fact]
    public async Task ARefundOfAWalletPaymentPutsTheMoneyBack()
    {
        var f = new Fixture();
        await f.FundAsync(100_000m);
        var paid = (await f.PayAsync(60_000m)).Value!;

        var result = await f.Provider().RefundAsync(paid, 25_000m, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(65_000m, f.Wallets.Balances[Buyer]);
        Assert.Equal(WalletEntryKind.Refund, f.Wallets.Entries.Last().Kind);
    }

    // ---- Topping up ----

    [Fact]
    public async Task ATopUpGoesThroughAGatewayPageAndAddsNothingUntilItIsPaid()
    {
        var f = new Fixture();

        var started = await f.Service().StartTopUpAsync(Buyer, new WalletTopUpCommand(200_000m, HostedSandboxPaymentProvider.Name, "topup-key-1"),
            CancellationToken.None);

        Assert.True(started.Succeeded);
        Assert.Equal(PaymentReferenceTypes.WalletTopUp, started.Value!.ReferenceType);
        Assert.Equal(Buyer, started.Value.ReferenceId);
        Assert.NotNull(started.Value.RedirectUrl);
        Assert.Empty(f.Wallets.Entries);
        Assert.Contains("wallet.topup_started", f.Audit.Events);
    }

    [Fact]
    public async Task APaidTopUpAddsItsAmountOnceHoweverOftenItIsReported()
    {
        var f = new Fixture();
        var handler = new WalletTopUpHandler(f.Wallets, new TestClock());
        var payment = new PaymentTransaction
        {
            Id = 5, ReferenceType = PaymentReferenceTypes.WalletTopUp, ReferenceId = Buyer, CustomerId = Buyer, Amount = 200_000m,
            Status = PaymentStatus.Paid, Method = HostedSandboxPaymentProvider.Name
        };

        await handler.OnCallbackAppliedAsync(payment, CancellationToken.None);
        await handler.OnCallbackAppliedAsync(payment, CancellationToken.None);

        Assert.Equal(200_000m, f.Wallets.Balances[Buyer]);
        Assert.Equal(WalletEntryKind.TopUp, Assert.Single(f.Wallets.Entries).Kind);
    }

    [Theory]
    [InlineData(PaymentReferenceTypes.WalletTopUp, PaymentStatus.Failed)]
    [InlineData(PaymentReferenceTypes.WalletTopUp, PaymentStatus.Pending)]
    [InlineData(PaymentReferenceTypes.Checkout, PaymentStatus.Paid)]
    public async Task OnlyAPaidTopUpAddsMoney(string referenceType, PaymentStatus status)
    {
        var f = new Fixture();
        var payment = new PaymentTransaction { Id = 5, ReferenceType = referenceType, ReferenceId = Buyer, CustomerId = Buyer, Amount = 1_000m, Status = status };

        await new WalletTopUpHandler(f.Wallets, new TestClock()).OnCallbackAppliedAsync(payment, CancellationToken.None);

        Assert.Empty(f.Wallets.Entries);
    }

    [Theory]
    [InlineData(9_999)]
    [InlineData(50_000_001)]
    [InlineData(10_000.5)]
    public async Task ATopUpOutsideTheLimitsIsRefused(double amount)
    {
        var f = new Fixture();

        var result = await f.Service().StartTopUpAsync(Buyer, new WalletTopUpCommand((decimal)amount, HostedSandboxPaymentProvider.Name, "topup-key-1"),
            CancellationToken.None);

        Assert.Contains("amount", result.Errors.Keys);
        Assert.Empty(f.PaymentStore.Payments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task ATopUpNeedsAKey(string? key)
    {
        var result = await new Fixture().Service().StartTopUpAsync(Buyer, new WalletTopUpCommand(100_000m, HostedSandboxPaymentProvider.Name, key),
            CancellationToken.None);

        Assert.Contains("idempotencyKey", result.Errors.Keys);
    }

    [Theory]
    [InlineData(WalletPaymentProvider.Name)]
    [InlineData("cod")]
    [InlineData("sandbox")]
    [InlineData("nope")]
    [InlineData(null)]
    public async Task OnlyAGatewayWithItsOwnPageCanTopUp(string? method)
    {
        var f = new Fixture();

        var result = await f.Service().StartTopUpAsync(Buyer, new WalletTopUpCommand(100_000m, method, "topup-key-1"), CancellationToken.None);

        Assert.Equal(WalletErrors.MethodNotAllowed, result.ErrorCode);
        Assert.Empty(f.PaymentStore.Payments);
    }

    // ---- The service ----

    [Fact]
    public async Task ACreditWithTheSameKeyAddsOnce()
    {
        var f = new Fixture();

        var first = await f.Service().CreditAsync(Buyer, WalletEntryKind.Refund, 30_000m, "return-1", "return", 1, "RT-1", CancellationToken.None);
        var second = await f.Service().CreditAsync(Buyer, WalletEntryKind.Refund, 30_000m, "return-1", "return", 1, "RT-1", CancellationToken.None);

        Assert.Equal((WalletPostOutcome.Posted, WalletPostOutcome.AlreadyPosted), (first.Outcome, second.Outcome));
        Assert.Equal(30_000m, f.Wallets.Balances[Buyer]);
        Assert.Single(f.Audit.Events, e => e == "wallet.credited");
    }

    [Fact]
    public async Task ANothingCreditChangesNothing()
    {
        var f = new Fixture();

        await f.Service().CreditAsync(Buyer, WalletEntryKind.Refund, 0m, "return-1", "return", 1, null, CancellationToken.None);

        Assert.Empty(f.Wallets.Entries);
    }

    [Fact]
    public async Task ACustomerWithoutAWalletHasNothingInIt()
    {
        var wallet = await new Fixture().Service().GetMineAsync(Buyer, CancellationToken.None);

        Assert.Equal(0m, wallet.Balance);
    }

    [Fact]
    public async Task TheHistoryIsNewestFirstAndOnlyTheCustomersOwn()
    {
        var f = new Fixture();
        await f.FundAsync(100_000m);
        await f.FundAsync(5_000m, Other);
        await f.PayAsync(30_000m);

        var page = await f.Service().GetEntriesAsync(Buyer, 0, 1000, CancellationToken.None);

        Assert.Equal([WalletEntryKind.Payment, WalletEntryKind.Adjustment], page.Items.Select(e => e.Kind));
        Assert.Equal((1, WalletLimits.MaxPageSize), (page.Page, page.PageSize));
    }

    // ---- A paid order cancelled ----

    private sealed class OneOrderMaintenance(params int[] paymentIds) : IMaintenanceStore
    {
        public Task<IReadOnlyList<int>> PaymentIdsForOrderAsync(int orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(orderId == 1 ? paymentIds : []);

        public Task<IReadOnlyList<int>> ExpiredAwaitingOrderIdsAsync(DateTime createdBeforeUtc, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> StalePaymentIdsAsync(DateTime changedBeforeUtc, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> DeliveredShopOrderIdsAsync(DateTime deliveredBeforeUtc, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CloseExpiredReservationsAsync(DateTime nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> DeleteClosedReservationsAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> DeleteStaleCartLinesAsync(DateTime untouchedSinceUtc, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> DeleteRunsAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static ShopOrder Cancelled(int id = 11, decimal total = 120_000m) => new()
    {
        Id = id, OrderId = 1, Total = total, Status = ShopOrderStatus.Cancelled,
        Order = new Order { Id = 1, Number = "NM-1", CustomerId = Buyer }
    };

    private static CancelledOrderWalletRefunds Refunds(Fixture f, PaymentStatus status, string method = "sandbox")
    {
        f.PaymentStore.Payments.Add(new PaymentTransaction { Id = 3, ReferenceType = PaymentReferenceTypes.Order, ReferenceId = 1, Method = method, Status = status, Amount = 240_000m });
        return new CancelledOrderWalletRefunds(new OneOrderMaintenance(3), f.PaymentStore, f.Wallets, f.Audit, new TestClock(),
            NullLogger<CancelledOrderWalletRefunds>.Instance);
    }

    [Theory]
    [InlineData(OrderActor.Customer)]
    [InlineData(OrderActor.Shop)]
    [InlineData(OrderActor.Admin)]
    public async Task APaidShopOrderCancelledGivesItsTotalBackToTheWalletOnce(OrderActor actor)
    {
        var f = new Fixture();
        var refunds = Refunds(f, PaymentStatus.Paid);

        await refunds.RefundCancelledAsync(Cancelled(), actor, CancellationToken.None);
        await refunds.RefundCancelledAsync(Cancelled(), actor, CancellationToken.None);

        var entry = Assert.Single(f.Wallets.Entries);
        Assert.Equal((Buyer, WalletEntryKind.Refund, 120_000m, "shop_order", 11), (entry.CustomerId, entry.Kind, entry.Amount, entry.ReferenceType, entry.ReferenceId));
        Assert.Single(f.Audit.Events, e => e == "order.cancel_refunded");
    }

    [Fact]
    public async Task EachCancelledShopOrderOfAnOrderGivesBackItsOwnPart()
    {
        var f = new Fixture();
        var refunds = Refunds(f, PaymentStatus.Paid, WalletPaymentProvider.Name);

        await refunds.RefundCancelledAsync(Cancelled(11, 120_000m), OrderActor.Customer, CancellationToken.None);
        await refunds.RefundCancelledAsync(Cancelled(12, 80_000m), OrderActor.Shop, CancellationToken.None);

        Assert.Equal(200_000m, f.Wallets.Balances[Buyer]);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Refunded)]
    public async Task NothingIsGivenBackWhenNothingWasPaid(PaymentStatus status)
    {
        var f = new Fixture();

        await Refunds(f, status, "cod").RefundCancelledAsync(Cancelled(), OrderActor.Customer, CancellationToken.None);

        Assert.Empty(f.Wallets.Entries);
    }

    [Fact]
    public async Task TheSystemsOwnCancelsAreLeftToThePaymentFlow()
    {
        var f = new Fixture();

        await Refunds(f, PaymentStatus.Paid).RefundCancelledAsync(Cancelled(), OrderActor.System, CancellationToken.None);

        Assert.Empty(f.Wallets.Entries);
    }
}
