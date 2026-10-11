using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Reports;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Reports;

/// <summary>
/// Read-only dashboards (F26-A). The service never decides who may look: the controller checks the shop membership or the permission, and the
/// shop id always comes from the route.
/// </summary>
public sealed class ReportService(
    IReportStore store,
    IOrderStore orders,
    IPrimaryCurrencyProvider primaryCurrency,
    IClock clock) : IReportService
{
    public async Task<ShopDashboard?> GetShopDashboardAsync(int vendorId, DateTime? firstDay, DateTime? lastDay, CancellationToken cancellationToken)
    {
        if (ReportRules.ResolveRange(firstDay, lastDay, clock.UtcNow) is not { } range) return null;
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);

        var summary = await store.GetShopSummaryAsync(vendorId, range.FromUtc, range.ToExclusiveUtc, cancellationToken);
        var daily = await store.GetDailySalesAsync(vendorId, range.FromUtc, range.ToExclusiveUtc, cancellationToken);
        var top = await store.GetTopProductsAsync(vendorId, range.FromUtc, range.ToExclusiveUtc, ReportLimits.TopRows, cancellationToken);
        var low = await store.GetLowStockAsync(vendorId, ReportLimits.LowStockRows, cancellationToken);
        var statuses = await orders.CountByStatusAsync(vendorId, cancellationToken);

        return new ShopDashboard(
            currency.Code, range.FromUtc, range.ToExclusiveUtc.AddDays(-1), summary, ReportRules.Average(summary.Sales, summary.Orders, currency.DecimalPlaces),
            ReportRules.FillDays(range, daily), top, statuses, low);
    }

    public async Task<PlatformDashboard?> GetPlatformDashboardAsync(DateTime? firstDay, DateTime? lastDay, CancellationToken cancellationToken)
    {
        if (ReportRules.ResolveRange(firstDay, lastDay, clock.UtcNow) is not { } range) return null;
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);

        var summary = await store.GetPlatformSummaryAsync(range.FromUtc, range.ToExclusiveUtc, cancellationToken);
        var daily = await store.GetDailySalesAsync(null, range.FromUtc, range.ToExclusiveUtc, cancellationToken);
        var shops = await store.GetTopShopsAsync(range.FromUtc, range.ToExclusiveUtc, ReportLimits.TopRows, cancellationToken);

        return new PlatformDashboard(
            currency.Code, range.FromUtc, range.ToExclusiveUtc.AddDays(-1), summary, ReportRules.Average(summary.Merchandise, summary.Orders, currency.DecimalPlaces),
            ReportRules.FillDays(range, daily), shops);
    }

    public Task<Operations> GetOperationsAsync(CancellationToken cancellationToken) => store.GetOperationsAsync(clock.UtcNow, cancellationToken);

    public async Task<Reconciliation?> GetReconciliationAsync(DateTime? firstDay, DateTime? lastDay, CancellationToken cancellationToken)
    {
        if (ReportRules.ResolveRange(firstDay, lastDay, clock.UtcNow) is not { } range) return null;
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        var report = await store.GetReconciliationAsync(range.FromUtc, range.ToExclusiveUtc, cancellationToken);
        return report with { CurrencyCode = currency.Code, FromUtc = range.FromUtc, ToUtc = range.ToExclusiveUtc.AddDays(-1) };
    }
}
