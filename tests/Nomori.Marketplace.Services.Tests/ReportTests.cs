using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Reports;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Reports;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ReportTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 15, 30, 0, DateTimeKind.Utc);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class FakeReportStore : IReportStore
    {
        public List<(string Call, int? Vendor, DateTime From, DateTime To)> Calls { get; } = [];
        public List<DailySales> Daily { get; } = [];

        public Task<ShopSummary> GetShopSummaryAsync(int vendorId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken)
        {
            Calls.Add(("summary", vendorId, fromUtc, toExclusiveUtc));
            return Task.FromResult(new ShopSummary(3, 7, 100m, 12m, 1, 2, 5m));
        }

        public Task<IReadOnlyList<DailySales>> GetDailySalesAsync(int? vendorId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken)
        {
            Calls.Add(("daily", vendorId, fromUtc, toExclusiveUtc));
            return Task.FromResult<IReadOnlyList<DailySales>>(Daily);
        }

        public Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(int vendorId, DateTime fromUtc, DateTime toExclusiveUtc, int take, CancellationToken cancellationToken)
        {
            Calls.Add(("top", vendorId, fromUtc, toExclusiveUtc));
            return Task.FromResult<IReadOnlyList<TopProduct>>([new TopProduct(1, "Mug", 5, 80m)]);
        }

        public Task<IReadOnlyList<LowStockProduct>> GetLowStockAsync(int vendorId, int take, CancellationToken cancellationToken)
        {
            Calls.Add(("low", vendorId, default, default));
            return Task.FromResult<IReadOnlyList<LowStockProduct>>([new LowStockProduct(2, "Tea", null, 1, 5)]);
        }

        public Task<PlatformSummary> GetPlatformSummaryAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new PlatformSummary(4, 400m, 2, 3, 40m, 10m, 1));

        public Task<IReadOnlyList<TopShop>> GetTopShopsAsync(DateTime fromUtc, DateTime toExclusiveUtc, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TopShop>>([new TopShop(5, "Shop", 4, 380m)]);

        public Task<Operations> GetOperationsAsync(DateTime nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new Operations(1, 50m, 2, 3, 4, 5, 6, 7, 8));

        public Task<Reconciliation> GetReconciliationAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new Reconciliation(string.Empty, fromUtc, toExclusiveUtc, 1, 100m, 0m, 10m, 5m, 6m, 101m, 95m, 10m, 0m, 0, 0m, 0m, 0));
    }

    private static ReportService Create(FakeReportStore store, OrderTests.FakeOrderStore? orders = null) =>
        new(store, orders ?? new OrderTests.FakeOrderStore(), new FakePrimaryCurrency(), new FixedClock());

    // ---- Rules ----

    [Fact]
    public void WithoutDatesTheRangeIsTheLastThirtyDaysIncludingToday()
    {
        var range = ReportRules.ResolveRange(null, null, Now)!;

        Assert.Equal(30, range.Days);
        Assert.Equal(new DateTime(2026, 2, 9, 0, 0, 0, DateTimeKind.Utc), range.FromUtc);
        Assert.Equal(new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc), range.ToExclusiveUtc);
    }

    [Fact]
    public void BothEndDaysAreIncludedAndTimesOfDayAreIgnored()
    {
        var range = ReportRules.ResolveRange(new DateTime(2026, 3, 1, 18, 0, 0), new DateTime(2026, 3, 3, 2, 0, 0), Now)!;

        Assert.Equal(3, range.Days);
        Assert.Equal(new DateTime(2026, 3, 4), range.ToExclusiveUtc);
        Assert.Equal(DateTimeKind.Utc, range.FromUtc.Kind);
    }

    [Fact]
    public void ARangeOfOneDayAndOfNinetyTwoDaysIsAllowedButNotMoreOrBackwards()
    {
        Assert.NotNull(ReportRules.ResolveRange(new DateTime(2026, 3, 1), new DateTime(2026, 3, 1), Now));
        Assert.NotNull(ReportRules.ResolveRange(new DateTime(2026, 1, 1), new DateTime(2026, 4, 2), Now)); // 92 days
        Assert.Null(ReportRules.ResolveRange(new DateTime(2026, 1, 1), new DateTime(2026, 4, 3), Now));   // 93 days
        Assert.Null(ReportRules.ResolveRange(new DateTime(2026, 3, 5), new DateTime(2026, 3, 1), Now));
    }

    [Fact]
    public void MissingDaysAreFilledWithZerosOldestFirst()
    {
        var range = ReportRules.ResolveRange(new DateTime(2026, 3, 1), new DateTime(2026, 3, 4), Now)!;

        var rows = ReportRules.FillDays(range, [new DailySales(new DateTime(2026, 3, 3), 2, 30m)]);

        Assert.Equal([0, 0, 2, 0], rows.Select(r => r.Orders));
        Assert.Equal(new DateTime(2026, 3, 1), rows[0].Day);
        Assert.Equal(30m, rows[2].Sales);
    }

    [Fact]
    public void TheAverageOrderValueIsZeroWithoutOrdersAndRounded()
    {
        Assert.Equal(0m, ReportRules.Average(100m, 0, 2));
        Assert.Equal(33.33m, ReportRules.Average(100m, 3, 2));
    }

    [Fact]
    public void TheDifferenceIsZeroWhenEveryTermIsAccountedFor()
    {
        // Customer paid 101 = shop 95 + commission 10 + tax 6 - platform discount 10.
        Assert.Equal(0m, ReportRules.Difference(101m, 95m, 10m, 6m, 10m));
        Assert.Equal(1m, ReportRules.Difference(102m, 95m, 10m, 6m, 10m));
        // With GHN: customer paid 106 = shop 95 + commission 10 + tax 6 - platform discount 10 + shipping kept to pay GHN 5.
        Assert.Equal(0m, ReportRules.Difference(106m, 95m, 10m, 6m, 10m, 5m));
    }

    // ---- Service ----

    [Fact]
    public async Task AShopDashboardAsksOnlyForItsOwnShopAndFillsTheDays()
    {
        var store = new FakeReportStore();
        store.Daily.Add(new DailySales(new DateTime(2026, 3, 9), 3, 100m));
        var orders = new OrderTests.FakeOrderStore();

        var dashboard = await Create(store, orders).GetShopDashboardAsync(5, new DateTime(2026, 3, 8), new DateTime(2026, 3, 10), CancellationToken.None);

        Assert.NotNull(dashboard);
        Assert.All(store.Calls, c => Assert.True(c.Vendor == 5));
        Assert.Equal(3, dashboard.Daily.Count);
        Assert.Equal(new DateTime(2026, 3, 10), dashboard.ToUtc);
        Assert.Equal(33.33m, dashboard.AverageOrderValue);
        Assert.Equal("USD", dashboard.CurrencyCode);
        Assert.Single(dashboard.TopProducts);
        Assert.Single(dashboard.LowStock);
    }

    [Fact]
    public async Task AWrongRangeGivesNoReport()
    {
        var service = Create(new FakeReportStore());

        Assert.Null(await service.GetShopDashboardAsync(5, new DateTime(2025, 1, 1), new DateTime(2026, 1, 1), CancellationToken.None));
        Assert.Null(await service.GetPlatformDashboardAsync(new DateTime(2026, 3, 5), new DateTime(2026, 3, 1), CancellationToken.None));
        Assert.Null(await service.GetReconciliationAsync(new DateTime(2025, 1, 1), null, CancellationToken.None));
    }

    [Fact]
    public async Task ThePlatformDashboardUsesTheMerchandiseForTheAverage()
    {
        var store = new FakeReportStore();

        var dashboard = await Create(store).GetPlatformDashboardAsync(new DateTime(2026, 3, 1), new DateTime(2026, 3, 2), CancellationToken.None);

        Assert.Equal(100m, dashboard!.AverageOrderValue);
        Assert.Equal(2, dashboard.Daily.Count);
        Assert.Single(dashboard.TopShops);
    }

    [Fact]
    public async Task TheReconciliationCarriesTheCurrencyAndTheLastDayOfTheRange()
    {
        var report = await Create(new FakeReportStore()).GetReconciliationAsync(new DateTime(2026, 3, 1), new DateTime(2026, 3, 3), CancellationToken.None);

        Assert.Equal("USD", report!.CurrencyCode);
        Assert.Equal(new DateTime(2026, 3, 3), report.ToUtc);
        Assert.Equal(0m, report.Difference);
    }

    [Fact]
    public async Task OperationsComeFromTheStore()
    {
        var operations = await Create(new FakeReportStore()).GetOperationsAsync(CancellationToken.None);

        Assert.Equal(1, operations.PendingPayouts);
        Assert.Equal(8, operations.LowStockProducts);
    }
}
