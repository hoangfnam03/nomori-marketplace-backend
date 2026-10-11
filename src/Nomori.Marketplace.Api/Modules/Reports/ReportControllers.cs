using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Reports;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Reports;

public sealed record ShopDashboardResponse(
    string CurrencyCode, DateTime FromUtc, DateTime ToUtc, ShopSummary Summary, decimal AverageOrderValue, IReadOnlyList<DailySales> Daily,
    IReadOnlyList<TopProduct> TopProducts, IReadOnlyDictionary<string, int> OrdersByStatus, IReadOnlyList<LowStockProduct> LowStock)
{
    public static ShopDashboardResponse From(ShopDashboard d) => new(
        d.CurrencyCode, d.FromUtc, d.ToUtc, d.Summary, d.AverageOrderValue, d.Daily, d.TopProducts,
        d.OrdersByStatus.ToDictionary(s => OrderRules.ToWire(s.Key), s => s.Value), d.LowStock);
}

internal static class ReportMapping
{
    public static IActionResult WrongRange(this ControllerBase controller) =>
        controller.BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["from"] = [ReportRules.Errors.Range] }));
}

/// <summary>The dashboard of one shop, for members of that shop. Anyone else gets 404, and so does another shop.</summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/reports")]
[Authorize]
public sealed class VendorReportsController(IReportService reports, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(int vendorId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        if (!caller.IsAuthenticated || !caller.IsMemberOf(vendorId)) return NotFound();
        return await reports.GetShopDashboardAsync(vendorId, from, to, cancellationToken) is { } dashboard
            ? Ok(ShopDashboardResponse.From(dashboard))
            : this.WrongRange();
    }
}

/// <summary>The reports of the whole platform, for administrators who may view them.</summary>
[ApiController]
[Route("api/v1/admin/reports")]
[Authorize]
[HasPermission(PermissionCodes.ReportsView)]
public sealed class AdminReportsController(IReportService reports, IVendorStore vendors) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken cancellationToken) =>
        await reports.GetPlatformDashboardAsync(from, to, cancellationToken) is { } dashboard ? Ok(dashboard) : this.WrongRange();

    [HttpGet("operations")]
    public async Task<IActionResult> Operations(CancellationToken cancellationToken) => Ok(await reports.GetOperationsAsync(cancellationToken));

    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken cancellationToken) =>
        await reports.GetReconciliationAsync(from, to, cancellationToken) is { } report ? Ok(report) : this.WrongRange();

    [HttpGet("vendors/{vendorId:int}/dashboard")]
    public async Task<IActionResult> VendorDashboard(int vendorId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken cancellationToken)
    {
        var vendor = await vendors.GetAsync(vendorId, cancellationToken);
        if (vendor is null || vendor.Deleted) return NotFound();
        return await reports.GetShopDashboardAsync(vendorId, from, to, cancellationToken) is { } dashboard
            ? Ok(ShopDashboardResponse.From(dashboard))
            : this.WrongRange();
    }
}
