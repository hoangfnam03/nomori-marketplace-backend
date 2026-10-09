using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Jobs;

namespace Nomori.Marketplace.Services.Tests;

/// <summary>The jobs that cancel unpaid orders, void stale payments and clean tables. They only call the services that own the rules.</summary>
public sealed class PaymentJobTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedNow : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class FakeMaintenance : IMaintenanceStore
    {
        public List<int> ExpiredOrders { get; } = [];
        public Dictionary<int, List<int>> PaymentsByOrder { get; } = new();
        public List<int> StalePayments { get; } = [];
        public int ExpiredHolds { get; set; }
        public int OldHolds { get; set; }
        public int OldCartLines { get; set; }
        public int OldRuns { get; set; }
        public Dictionary<string, (DateTime Before, int Take)> Calls { get; } = new();

        public Task<IReadOnlyList<int>> ExpiredAwaitingOrderIdsAsync(DateTime createdBeforeUtc, int take, CancellationToken cancellationToken)
        {
            Calls["orders"] = (createdBeforeUtc, take);
            return Task.FromResult<IReadOnlyList<int>>(ExpiredOrders.Take(take).ToList());
        }

        public Task<IReadOnlyList<int>> PaymentIdsForOrderAsync(int orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(PaymentsByOrder.GetValueOrDefault(orderId) ?? []);

        public Task<IReadOnlyList<int>> StalePaymentIdsAsync(DateTime changedBeforeUtc, int take, CancellationToken cancellationToken)
        {
            Calls["payments"] = (changedBeforeUtc, take);
            return Task.FromResult<IReadOnlyList<int>>(StalePayments.Take(take).ToList());
        }

        public Task<int> CloseExpiredReservationsAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            Calls["close"] = (nowUtc, 0);
            return Task.FromResult(ExpiredHolds);
        }

        public Task<int> DeleteClosedReservationsAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken)
        {
            Calls["holds"] = (beforeUtc, take);
            return Task.FromResult(OldHolds);
        }

        public Task<int> DeleteStaleCartLinesAsync(DateTime untouchedSinceUtc, int take, CancellationToken cancellationToken)
        {
            Calls["carts"] = (untouchedSinceUtc, take);
            return Task.FromResult(OldCartLines);
        }

        public Task<int> DeleteRunsAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken)
        {
            Calls["runs"] = (beforeUtc, take);
            return Task.FromResult(OldRuns);
        }
    }

    private sealed class FakeOrders : IOrderService
    {
        public List<(int OrderId, string Reason)> Cancelled { get; } = [];
        public HashSet<int> Throwing { get; } = [];
        public HashSet<int> Refusing { get; } = [];

        public Task<CatalogResult<Order>> CancelAsSystemAsync(int orderId, string reason, CancellationToken cancellationToken)
        {
            if (Throwing.Contains(orderId)) throw new InvalidOperationException("database down");
            if (Refusing.Contains(orderId)) return Task.FromResult(CatalogResult.Error<Order>(CatalogErrors.NotFound));
            Cancelled.Add((orderId, reason));
            return Task.FromResult(CatalogResult.Success(new Order { Id = orderId }));
        }

        public Task<CatalogResult<Order>> CreateAsync(NewOrderCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Order?> FindByPlacementKeyAsync(string placementKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<bool>> MarkPaidAsync(int orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<Order>> GetMyOrdersAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<OrderDetail>> GetMyOrderAsync(int customerId, int orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> CancelAsCustomerAsync(int customerId, int shopOrderId, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> ConfirmReceiptAsync(int customerId, int shopOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<ShopOrder>> GetShopOrdersAsync(int vendorId, OrderListQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<ShopOrderStatus, int>> GetCountsAsync(int vendorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> GetShopOrderAsync(int vendorId, int shopOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> ConfirmAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> ShipAsync(int vendorId, int shopOrderId, ShipCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> UpdateTrackingAsync(int vendorId, int shopOrderId, ShipCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> DeliverAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> CancelAsShopAsync(int vendorId, int shopOrderId, string? reason, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<Order>> GetOrdersAsync(OrderListQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<OrderDetail>> GetOrderAsync(int orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShopOrderDetail>> CancelAsAdminAsync(int shopOrderId, string? reason, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeDiscounts : IDiscountService
    {
        public List<int> Released { get; } = [];

        public Task ReleaseAsync(int orderId, CancellationToken cancellationToken)
        {
            Released.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Discount>> GetListAsync(int? vendorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<Discount>> CreateAsync(int? vendorId, SaveDiscountCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<Discount>> UpdateAsync(int? vendorId, int id, SaveDiscountCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<bool>> DeleteAsync(int? vendorId, int id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CouponCheck> CheckCouponAsync(string? code, int customerId, IReadOnlyDictionary<int, decimal> shopSubtotals, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RedeemOutcome> RedeemAsync(AppliedDiscount applied, int customerId, int orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CouponOffer>> GetOffersAsync(int customerId, IReadOnlyDictionary<int, decimal> shopSubtotals, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePayments : IPaymentService
    {
        public Dictionary<int, PaymentTransaction> Payments { get; } = new();
        public List<(int Id, decimal Amount, int Actor)> Refunds { get; } = [];
        public List<(int Id, int Actor)> Voids { get; } = [];
        public HashSet<int> RefusingRefund { get; } = [];
        public HashSet<int> RefusingVoid { get; } = [];
        public HashSet<int> ChangedMeanwhile { get; } = [];

        public Task<PaymentTransaction?> GetPaymentAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(Payments.GetValueOrDefault(id));

        public Task<CatalogResult<PaymentTransaction>> RefundAsync(int id, decimal amount, int actorCustomerId, CancellationToken cancellationToken)
        {
            if (RefusingRefund.Contains(id)) return Task.FromResult(CatalogResult.Error<PaymentTransaction>(PaymentErrors.ProviderFailed));
            Refunds.Add((id, amount, actorCustomerId));
            Payments[id].Status = PaymentStatus.Refunded;
            return Task.FromResult(CatalogResult.Success(Payments[id]));
        }

        public Task<CatalogResult<PaymentTransaction>> VoidAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
        {
            if (ChangedMeanwhile.Contains(id)) return Task.FromResult(CatalogResult.Error<PaymentTransaction>(PaymentErrors.InvalidState));
            if (RefusingVoid.Contains(id)) return Task.FromResult(CatalogResult.Error<PaymentTransaction>(PaymentErrors.ProviderFailed));
            Voids.Add((id, actorCustomerId));
            Payments[id].Status = PaymentStatus.Voided;
            return Task.FromResult(CatalogResult.Success(Payments[id]));
        }

        public Task<IReadOnlyList<PaymentMethodView>> GetAvailableMethodsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<PaymentTransaction>> CreateAsync(CreatePaymentCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PaymentMethodView>> GetMethodsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<PaymentMethodView>> UpdateMethodAsync(string systemName, bool enabled, int displayOrder, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<PaymentTransaction>> GetPaymentsAsync(PaymentQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<PaymentTransaction>> CaptureAsync(int id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public string? GetRedirectUrl(PaymentTransaction payment) => throw new NotSupportedException();
        public Task<PaymentTransaction?> FindByProviderReferenceAsync(string method, string providerReference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CallbackOutcome> HandleCallbackAsync(string provider, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public FakeMaintenance Maintenance { get; } = new();
        public FakeOrders Orders { get; } = new();
        public FakeDiscounts Discounts { get; } = new();
        public FakePayments Payments { get; } = new();
        public JobOptions Options { get; } = new();

        public ExpireUnpaidOrdersJob Expire() =>
            new(Maintenance, Orders, Discounts, Payments, new FixedNow(), Microsoft.Extensions.Options.Options.Create(Options), NullLogger<ExpireUnpaidOrdersJob>.Instance);

        public VoidStalePaymentsJob VoidStale() =>
            new(Maintenance, Payments, new FixedNow(), Microsoft.Extensions.Options.Options.Create(Options), NullLogger<VoidStalePaymentsJob>.Instance);

        public PaymentTransaction AddPayment(int id, PaymentStatus status, decimal amount = 100m)
        {
            var payment = new PaymentTransaction { Id = id, Status = status, Amount = amount, ReferenceType = PaymentReferenceTypes.Order };
            Payments.Payments[id] = payment;
            return payment;
        }
    }

    // ---- orders.expire_unpaid ----

    [Fact]
    public async Task UnpaidOrdersOlderThanTheAllowedTimeAreListed()
    {
        var fixture = new Fixture();
        fixture.Options.AwaitingPaymentMinutes = 45;

        await fixture.Expire().RunAsync(default);

        Assert.Equal((Now.AddMinutes(-45), JobRules.BatchSize), fixture.Maintenance.Calls["orders"]);
    }

    [Fact]
    public async Task NothingToExpireIsACleanRunWithoutANote()
    {
        var result = await new Fixture().Expire().RunAsync(default);

        Assert.Equal(new JobResult(0), result);
    }

    [Fact]
    public async Task AnExpiredOrderIsCancelledBySystemAndItsDiscountUseIsGivenBack()
    {
        var fixture = new Fixture();
        fixture.Maintenance.ExpiredOrders.AddRange([11, 12]);

        var result = await fixture.Expire().RunAsync(default);

        Assert.Equal([(11, "Payment not completed in time"), (12, "Payment not completed in time")], fixture.Orders.Cancelled);
        Assert.Equal([11, 12], fixture.Discounts.Released);
        Assert.Equal((2, 0), (result.Processed, result.Failed));
    }

    [Fact]
    public async Task ACancelledOrderWhoseMoneyCameInIsRefundedInFullAndNothingElseIsTouched()
    {
        var fixture = new Fixture();
        fixture.Maintenance.ExpiredOrders.Add(11);
        // A callback paid the order between the listing and the cancel: it has been paid 100 and refunded 30 before.
        var paid = fixture.AddPayment(1, PaymentStatus.PartiallyRefunded, 100m);
        paid.RefundedAmount = 30m;
        fixture.AddPayment(2, PaymentStatus.Pending);
        fixture.AddPayment(3, PaymentStatus.Voided);
        fixture.Maintenance.PaymentsByOrder[11] = [1, 2, 3];

        var result = await fixture.Expire().RunAsync(default);

        // Only what is still refundable goes back, and as the system (actor 0).
        Assert.Equal([(1, 70m, 0)], fixture.Payments.Refunds);
        Assert.Empty(fixture.Payments.Voids);
        Assert.Equal((1, 0), (result.Processed, result.Failed));
    }

    [Fact]
    public async Task APendingPaymentIsLeftForTheLateCallbackOrTheStaleJob()
    {
        var fixture = new Fixture();
        fixture.Maintenance.ExpiredOrders.Add(11);
        fixture.AddPayment(1, PaymentStatus.Pending);
        fixture.Maintenance.PaymentsByOrder[11] = [1];

        await fixture.Expire().RunAsync(default);

        Assert.Empty(fixture.Payments.Refunds);
        Assert.Empty(fixture.Payments.Voids);
        Assert.Equal(PaymentStatus.Pending, fixture.Payments.Payments[1].Status);
    }

    [Fact]
    public async Task ARefundThatFailsIsCountedAsAFailureEvenThoughTheOrderIsCancelled()
    {
        var fixture = new Fixture();
        fixture.Maintenance.ExpiredOrders.AddRange([11, 12]);
        fixture.AddPayment(1, PaymentStatus.Paid);
        fixture.Payments.RefusingRefund.Add(1);
        fixture.Maintenance.PaymentsByOrder[11] = [1];

        var result = await fixture.Expire().RunAsync(default);

        Assert.Equal([11, 12], fixture.Orders.Cancelled.Select(c => c.OrderId));
        Assert.Equal((1, 1), (result.Processed, result.Failed));
    }

    [Fact]
    public async Task OneOrderThatCannotBeCancelledDoesNotStopTheOthers()
    {
        var fixture = new Fixture();
        fixture.Maintenance.ExpiredOrders.AddRange([11, 12, 13]);
        fixture.Orders.Throwing.Add(11);
        fixture.Orders.Refusing.Add(12);

        var result = await fixture.Expire().RunAsync(default);

        Assert.Equal([13], fixture.Orders.Cancelled.Select(c => c.OrderId));
        // No discount use is given back for an order that was not cancelled.
        Assert.Equal([13], fixture.Discounts.Released);
        Assert.Equal((1, 2), (result.Processed, result.Failed));
        Assert.Equal(JobStatuses.Partial, JobRules.StatusOf(result));
    }

    [Fact]
    public async Task AnExpiryRunHandlesAtMostOneBatch()
    {
        var fixture = new Fixture();
        fixture.Maintenance.ExpiredOrders.AddRange(Enumerable.Range(1, JobRules.BatchSize + 50));

        var result = await fixture.Expire().RunAsync(default);

        Assert.Equal(JobRules.BatchSize, result.Processed);
    }

    // ---- payments.void_stale ----

    [Fact]
    public async Task StalePaymentsAreThoseQuietForTheGracePeriod()
    {
        var fixture = new Fixture();
        fixture.Options.StalePaymentGraceHours = 6;

        await fixture.VoidStale().RunAsync(default);

        Assert.Equal((Now.AddHours(-6), JobRules.BatchSize), fixture.Maintenance.Calls["payments"]);
    }

    [Fact]
    public async Task StalePaymentsAreVoidedAsTheSystem()
    {
        var fixture = new Fixture();
        fixture.Maintenance.StalePayments.AddRange([1, 2]);
        fixture.AddPayment(1, PaymentStatus.Pending);
        fixture.AddPayment(2, PaymentStatus.Authorized);

        var result = await fixture.VoidStale().RunAsync(default);

        Assert.Equal([(1, 0), (2, 0)], fixture.Payments.Voids);
        Assert.Equal((2, 0), (result.Processed, result.Failed));
    }

    [Fact]
    public async Task APaymentThatChangedMeanwhileIsNotAFailure()
    {
        var fixture = new Fixture();
        fixture.Maintenance.StalePayments.AddRange([1, 2]);
        fixture.AddPayment(1, PaymentStatus.Pending);
        fixture.AddPayment(2, PaymentStatus.Pending);
        fixture.Payments.ChangedMeanwhile.Add(1);

        var result = await fixture.VoidStale().RunAsync(default);

        Assert.Equal([(2, 0)], fixture.Payments.Voids);
        Assert.Equal((2, 0), (result.Processed, result.Failed));
    }

    [Fact]
    public async Task AProviderThatRefusesTheVoidIsCountedAndTheNextPaymentStillRuns()
    {
        var fixture = new Fixture();
        fixture.Maintenance.StalePayments.AddRange([1, 2]);
        fixture.AddPayment(1, PaymentStatus.Pending);
        fixture.AddPayment(2, PaymentStatus.Pending);
        fixture.Payments.RefusingVoid.Add(1);

        var result = await fixture.VoidStale().RunAsync(default);

        Assert.Equal([(2, 0)], fixture.Payments.Voids);
        Assert.Equal((1, 1), (result.Processed, result.Failed));
    }

    // ---- Housekeeping ----

    [Fact]
    public async Task ReservationsAreClosedAtOnceAndOldClosedOnesAreDeletedAfterTheRetention()
    {
        var maintenance = new FakeMaintenance { ExpiredHolds = 3, OldHolds = 4 };
        var options = new JobOptions { ReservationRetentionDays = 10 };

        var result = await new PurgeReservationsJob(maintenance, new FixedNow(), Options.Create(options)).RunAsync(default);

        Assert.Equal(Now, maintenance.Calls["close"].Before);
        Assert.Equal((Now.AddDays(-10), JobRules.BatchSize), maintenance.Calls["holds"]);
        Assert.Equal(7, result.Processed);
        Assert.Equal("Closed 3 expired holds, deleted 4 old ones.", result.Message);
    }

    [Fact]
    public async Task StaleCartLinesAreDeletedAfterTheRetention()
    {
        var maintenance = new FakeMaintenance { OldCartLines = 9 };

        var result = await new PurgeStaleCartsJob(maintenance, new FixedNow(), Options.Create(new JobOptions { CartRetentionDays = 60 })).RunAsync(default);

        Assert.Equal((Now.AddDays(-60), JobRules.BatchSize), maintenance.Calls["carts"]);
        Assert.Equal(9, result.Processed);
    }

    [Fact]
    public async Task OldRunHistoryIsDeletedAfterTheRetention()
    {
        var maintenance = new FakeMaintenance { OldRuns = 12 };

        var result = await new PurgeJobHistoryJob(maintenance, new FixedNow(), Options.Create(new JobOptions { RunRetentionDays = 14 })).RunAsync(default);

        Assert.Equal(Now.AddDays(-14), maintenance.Calls["runs"].Before);
        Assert.Equal(12, result.Processed);
    }

    [Fact]
    public async Task NothingToCleanIsACleanRunWithoutANote()
    {
        var maintenance = new FakeMaintenance();
        var clock = new FixedNow();
        var options = Options.Create(new JobOptions());

        Assert.Equal(new JobResult(0), await new PurgeReservationsJob(maintenance, clock, options).RunAsync(default));
        Assert.Equal(new JobResult(0), await new PurgeStaleCartsJob(maintenance, clock, options).RunAsync(default));
        Assert.Equal(new JobResult(0), await new PurgeJobHistoryJob(maintenance, clock, options).RunAsync(default));
    }

    [Fact]
    public void EveryJobHasAUniqueStableNameAndAValidDefaultInterval()
    {
        var f = new Fixture();
        IScheduledJob[] jobs =
        [
            f.Expire(), f.VoidStale(),
            new PurgeReservationsJob(f.Maintenance, new FixedNow(), Options.Create(new JobOptions())),
            new PurgeStaleCartsJob(f.Maintenance, new FixedNow(), Options.Create(new JobOptions())),
            new PurgeJobHistoryJob(f.Maintenance, new FixedNow(), Options.Create(new JobOptions()))
        ];

        Assert.Equal(
            ["orders.expire_unpaid", "payments.void_stale", "inventory.purge_reservations", "cart.purge_stale", "jobs.purge_history"],
            jobs.Select(j => j.Name));
        Assert.All(jobs, j => Assert.True(JobRules.IsValidInterval(j.DefaultIntervalMinutes)));
        Assert.All(jobs, j => Assert.False(string.IsNullOrWhiteSpace(j.Description)));
    }
}
