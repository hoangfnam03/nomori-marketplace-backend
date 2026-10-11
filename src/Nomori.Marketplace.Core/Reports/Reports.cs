using Nomori.Marketplace.Core.Orders;

namespace Nomori.Marketplace.Core.Reports;

public static class ReportLimits
{
    public const int MaxDays = 92;
    public const int DefaultDays = 30;
    public const int TopRows = 10;
    public const int LowStockRows = 20;

    /// <summary>A shop order that waits this long for the shop to confirm it is reported as late.</summary>
    public const int UnconfirmedHours = 24;

    public const int RecentJobHours = 24;
}

public sealed record ReportRange(DateTime FromUtc, DateTime ToExclusiveUtc, int Days);

public sealed record DailySales(DateTime Day, int Orders, decimal Sales);

public sealed record ShopSummary(
    int Orders, int Units, decimal Sales, decimal Shipping, int CancelledOrders, int ReturnsOpened, decimal RefundedAmount);

public sealed record TopProduct(int ProductId, string Name, int Units, decimal Revenue);

public sealed record LowStockProduct(int ProductId, string Name, string? Sku, int StockQuantity, int LowStockThreshold);

public sealed record ShopDashboard(
    string CurrencyCode, DateTime FromUtc, DateTime ToUtc, ShopSummary Summary, decimal AverageOrderValue, IReadOnlyList<DailySales> Daily,
    IReadOnlyList<TopProduct> TopProducts, IReadOnlyDictionary<ShopOrderStatus, int> OrdersByStatus, IReadOnlyList<LowStockProduct> LowStock);

public sealed record PlatformSummary(
    int Orders, decimal Merchandise, int NewCustomers, int ActiveShops, decimal Commission, decimal RefundedAmount, int CancelledOrders);

public sealed record TopShop(int VendorId, string ShopName, int Orders, decimal Sales);

public sealed record PlatformDashboard(
    string CurrencyCode, DateTime FromUtc, DateTime ToUtc, PlatformSummary Summary, decimal AverageOrderValue, IReadOnlyList<DailySales> Daily,
    IReadOnlyList<TopShop> TopShops);

/// <summary>What needs a person's attention right now.</summary>
public sealed record Operations(
    int PendingPayouts, decimal PendingPayoutAmount, int OpenReturns, int UnconfirmedShopOrders, int FailedJobs, int FailedEmails,
    int UnrecordedSales, int UnrecordedRefunds, int LowStockProducts);

/// <summary>
/// The sale lines of the ledger in a range against the orders they come from. <see cref="Difference"/> must be 0; anything else is a defect.
/// </summary>
public sealed record Reconciliation(
    string CurrencyCode, DateTime FromUtc, DateTime ToUtc, int SaleLines, decimal Items, decimal ShopFundedDiscount, decimal PlatformFundedDiscount,
    decimal Shipping, decimal Tax, decimal CustomerPaid, decimal ShopRevenue, decimal Commission, decimal Difference,
    int RefundLines, decimal RefundedFromShops, decimal CommissionReturned, int PayoutMismatches);

public static class ReportRules
{
    public static class Errors
    {
        public const string Range = "Choose a range of 1 to 92 days, with the first day not after the last.";
    }

    /// <summary>
    /// Both dates are days (UTC) and both are included. Without dates the range is the last 30 days up to and including today.
    /// Null when the range is wrong.
    /// </summary>
    public static ReportRange? ResolveRange(DateTime? from, DateTime? to, DateTime nowUtc)
    {
        var last = (to ?? nowUtc).Date;
        var first = (from ?? last.AddDays(-(ReportLimits.DefaultDays - 1))).Date;
        var days = (last - first).Days + 1;
        if (days < 1 || days > ReportLimits.MaxDays) return null;
        return new ReportRange(DateTime.SpecifyKind(first, DateTimeKind.Utc), DateTime.SpecifyKind(last.AddDays(1), DateTimeKind.Utc), days);
    }

    /// <summary>One row for every day of the range, oldest first; days without sales are zero.</summary>
    public static IReadOnlyList<DailySales> FillDays(ReportRange range, IEnumerable<DailySales> rows)
    {
        var byDay = rows.ToDictionary(r => r.Day.Date);
        var filled = new List<DailySales>(range.Days);
        for (var i = 0; i < range.Days; i++)
        {
            var day = range.FromUtc.AddDays(i).Date;
            filled.Add(byDay.TryGetValue(day, out var row) ? row with { Day = DateTime.SpecifyKind(day, DateTimeKind.Utc) } : new DailySales(DateTime.SpecifyKind(day, DateTimeKind.Utc), 0, 0m));
        }
        return filled;
    }

    public static decimal Average(decimal sales, int orders, int places) =>
        orders == 0 ? 0m : Math.Round(sales / orders, places, MidpointRounding.AwayFromZero);

    /// <summary>What the customers paid, less everything that is accounted for. 0 when the books agree.</summary>
    public static decimal Difference(decimal customerPaid, decimal shopRevenue, decimal commission, decimal tax, decimal platformFundedDiscount) =>
        customerPaid - (shopRevenue + commission + tax - platformFundedDiscount);
}

public interface IReportStore
{
    Task<ShopSummary> GetShopSummaryAsync(int vendorId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken);

    /// <summary>Daily sales of one shop, or of the platform when <paramref name="vendorId"/> is null. Only days with sales.</summary>
    Task<IReadOnlyList<DailySales>> GetDailySalesAsync(int? vendorId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(int vendorId, DateTime fromUtc, DateTime toExclusiveUtc, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<LowStockProduct>> GetLowStockAsync(int vendorId, int take, CancellationToken cancellationToken);

    Task<PlatformSummary> GetPlatformSummaryAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<TopShop>> GetTopShopsAsync(DateTime fromUtc, DateTime toExclusiveUtc, int take, CancellationToken cancellationToken);
    Task<Operations> GetOperationsAsync(DateTime nowUtc, CancellationToken cancellationToken);
    Task<Reconciliation> GetReconciliationAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken);
}

public interface IReportService
{
    /// <summary>The caller has been checked against the shop. Null when the range is wrong.</summary>
    Task<ShopDashboard?> GetShopDashboardAsync(int vendorId, DateTime? firstDay, DateTime? lastDay, CancellationToken cancellationToken);

    Task<PlatformDashboard?> GetPlatformDashboardAsync(DateTime? firstDay, DateTime? lastDay, CancellationToken cancellationToken);
    Task<Operations> GetOperationsAsync(CancellationToken cancellationToken);
    Task<Reconciliation?> GetReconciliationAsync(DateTime? firstDay, DateTime? lastDay, CancellationToken cancellationToken);
}
