using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Settlement;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Settlement;

public sealed record EntryResponse(
    long Id, int VendorId, string Kind, int? ShopOrderId, string? ShopOrderNumber, int? ReturnRequestId, decimal Gross, decimal Commission, decimal Amount,
    string CurrencyCode, string? Note, int? PayoutId, DateTime CreatedOnUtc)
{
    public static EntryResponse From(SettlementEntry e) => new(
        e.Id, e.VendorId, SettlementRules.ToWire(e.Kind), e.ShopOrderId, e.ShopOrderNumber, e.ReturnRequestId, e.Gross, e.Commission, e.Amount,
        e.CurrencyCode, e.Note, e.PayoutId, e.CreatedOnUtc);
}

public sealed record EntryPageResponse(IReadOnlyList<EntryResponse> Items, int TotalCount, int Page, int PageSize, int TotalPages)
{
    public static EntryPageResponse From(PagedResult<SettlementEntry> page) =>
        new(page.Items.Select(EntryResponse.From).ToList(), page.TotalCount, page.Page, page.PageSize, page.TotalPages);
}

/// <summary>A payout never shows the account number, only its last 4 digits.</summary>
public sealed record PayoutResponse(
    int Id, string Number, int VendorId, string ShopName, string Status, decimal Amount, string CurrencyCode, int EntryCount, DateTime CutoffUtc,
    string BankName, string AccountLast4, string HolderName, string? Branch, string? Reference, string? FailureReason, DateTime CreatedOnUtc, DateTime UpdatedOnUtc)
{
    public static PayoutResponse From(Payout p) => new(
        p.Id, p.Number, p.VendorId, p.ShopName, SettlementRules.ToWire(p.Status), p.Amount, p.CurrencyCode, p.EntryCount, p.CutoffUtc,
        p.BankName, p.AccountLast4, p.HolderName, p.Branch, p.Reference, p.FailureReason, p.CreatedOnUtc, p.UpdatedOnUtc);
}

public sealed record PayoutPageResponse(IReadOnlyList<PayoutResponse> Items, int TotalCount, int Page, int PageSize, int TotalPages)
{
    public static PayoutPageResponse From(PagedResult<Payout> page) =>
        new(page.Items.Select(PayoutResponse.From).ToList(), page.TotalCount, page.Page, page.PageSize, page.TotalPages);
}

public sealed record PayoutDetailResponse(PayoutResponse Payout, IReadOnlyList<EntryResponse> Entries)
{
    public static PayoutDetailResponse From(PayoutDetail detail) => new(PayoutResponse.From(detail.Payout), detail.Entries.Select(EntryResponse.From).ToList());
}

public sealed record PayoutRunResponse(IReadOnlyList<PayoutResponse> Created, int Skipped);

public sealed record RateBody(decimal Rate, string? Note);

public sealed record VendorRateBody(decimal? Rate, string? Note);

public sealed record AdjustmentBody(int VendorId, decimal Amount, string? Reason);

public sealed record CreatePayoutsBody(DateTime? CutoffUtc);

public sealed record MarkPaidBody(string? Reference);

public sealed record MarkFailedBody(string? Reason);

public sealed record BankAccountBody(string? BankName, string? AccountNumber, string? HolderName, string? Branch, string? Password);

internal static class SettlementMapping
{
    public static IActionResult Respond<T>(this ControllerBase controller, CatalogResult<T> result, Func<T, object> map) =>
        result.Succeeded ? controller.Ok(map(result.Value!)) : controller.ToFailure(result);

    public static FileContentResult Csv(this ControllerBase controller, string csv, string name) =>
        // The byte order mark lets a spreadsheet read the file as UTF-8.
        controller.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", name);
}

/// <summary>The money of one shop, for members of that shop. Anyone else gets 404, and so does another shop.</summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/finance")]
[Authorize]
public sealed class VendorFinanceController(ISettlementService settlement, IVendorAccessContext accessContext, ICurrentUser currentUser) : ControllerBase
{
    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> Overview(int vendorId, CancellationToken cancellationToken) =>
        await IsMemberAsync(vendorId, cancellationToken) ? Ok(await settlement.GetOverviewAsync(vendorId, cancellationToken)) : NotFound();

    [HttpGet("ledger")]
    public async Task<IActionResult> Ledger(
        int vendorId, [FromQuery] string? kind, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!await IsMemberAsync(vendorId, cancellationToken)) return NotFound();
        EntryKind? parsed = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!SettlementRules.TryParse(kind, out EntryKind value)) return this.ToFailure(CatalogResult.Failure<object>("kind", "Unknown type."));
            parsed = value;
        }
        return Ok(EntryPageResponse.From(await settlement.GetEntriesAsync(new EntryQuery(vendorId, parsed, from, to, search, page, pageSize), cancellationToken)));
    }

    [HttpGet("payouts")]
    public async Task<IActionResult> Payouts(int vendorId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        await IsMemberAsync(vendorId, cancellationToken)
            ? Ok(PayoutPageResponse.From(await settlement.GetPayoutsAsync(new SettlementCaller(false, CustomerId, vendorId), null, page, pageSize, cancellationToken)))
            : NotFound();

    [HttpGet("payouts/{payoutId:int}")]
    public async Task<IActionResult> Payout(int vendorId, int payoutId, CancellationToken cancellationToken) =>
        await IsMemberAsync(vendorId, cancellationToken)
            ? this.Respond(await settlement.GetPayoutAsync(new SettlementCaller(false, CustomerId, vendorId), payoutId, cancellationToken), PayoutDetailResponse.From)
            : NotFound();

    [HttpGet("payouts/{payoutId:int}/csv")]
    public async Task<IActionResult> PayoutCsv(int vendorId, int payoutId, CancellationToken cancellationToken)
    {
        if (!await IsMemberAsync(vendorId, cancellationToken)) return NotFound();
        var result = await settlement.ExportPayoutCsvAsync(vendorId, payoutId, cancellationToken);
        return result.Succeeded ? this.Csv(result.Value!, $"payout-{payoutId}.csv") : this.ToFailure(result);
    }

    [HttpGet("bank-account")]
    public async Task<IActionResult> BankAccount(int vendorId, CancellationToken cancellationToken)
    {
        if (!await IsMemberAsync(vendorId, cancellationToken)) return NotFound();
        return await settlement.GetBankAccountAsync(vendorId, cancellationToken) is { } view ? Ok(view) : NoContent();
    }

    [HttpPut("bank-account")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBankAccount(int vendorId, BankAccountBody body, CancellationToken cancellationToken) =>
        await IsMemberAsync(vendorId, cancellationToken)
            ? this.Respond(await settlement.SaveBankAccountAsync(
                vendorId, new SaveBankAccountCommand(body.BankName, body.AccountNumber, body.HolderName, body.Branch, body.Password), CustomerId, cancellationToken), v => v)
            : NotFound();

    private async Task<bool> IsMemberAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId);
    }
}

/// <summary>The banks a shop can choose from.</summary>
[ApiController]
[Route("api/v1/finance/banks")]
[Authorize]
public sealed class FinanceBanksController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(SettlementBanks.All);
}

/// <summary>Commission, adjustments and payouts of the whole platform, for administrators who may manage finance.</summary>
[ApiController]
[Route("api/v1/admin/finance")]
[Authorize]
[HasPermission(PermissionCodes.FinanceManage)]
public sealed class AdminFinanceController(ISettlementService settlement, ICurrentUser currentUser) : ControllerBase
{
    private int AdminId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpGet("commission")]
    public async Task<IActionResult> Commission(CancellationToken cancellationToken) => Ok(await settlement.GetCommissionAsync(cancellationToken));

    [HttpPut("commission/default")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefault(RateBody body, CancellationToken cancellationToken) =>
        this.Respond(await settlement.SetDefaultRateAsync(body.Rate, body.Note, AdminId, cancellationToken), rate => new { defaultRate = rate });

    [HttpPut("commission/vendors/{vendorId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetVendor(int vendorId, VendorRateBody body, CancellationToken cancellationToken) =>
        body.Rate is null
            ? this.Respond(CatalogResult.Failure<decimal?>("rate", "Enter a rate. Use DELETE to remove the shop's own rate."), r => new { rate = r })
            : this.Respond(await settlement.SetVendorRateAsync(vendorId, body.Rate, body.Note, AdminId, cancellationToken), rate => new { rate });

    [HttpDelete("commission/vendors/{vendorId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveVendor(int vendorId, [FromQuery] string? note, CancellationToken cancellationToken) =>
        this.Respond(await settlement.SetVendorRateAsync(vendorId, null, note, AdminId, cancellationToken), rate => new { rate });

    [HttpGet("balances")]
    public async Task<IActionResult> Balances(
        [FromQuery] string? bank, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(bank) && bank is not ("missing" or "hold" or "ready"))
            return this.ToFailure(CatalogResult.Failure<object>("bank", "Unknown bank status."));
        return Ok(await settlement.GetBalancesAsync(new BalanceQuery(bank, search, page, pageSize), cancellationToken));
    }

    [HttpGet("monthly")]
    public async Task<IActionResult> Monthly([FromQuery] int months = 12, CancellationToken cancellationToken = default) =>
        Ok(await settlement.GetPlatformMonthlyAsync(months, cancellationToken));

    [HttpGet("vendors/{vendorId:int}")]
    public async Task<IActionResult> VendorOverview(int vendorId, CancellationToken cancellationToken) =>
        Ok(await settlement.GetOverviewAsync(vendorId, cancellationToken));

    [HttpGet("vendors/{vendorId:int}/ledger")]
    public async Task<IActionResult> VendorLedger(
        int vendorId, [FromQuery] string? kind, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        EntryKind? parsed = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!SettlementRules.TryParse(kind, out EntryKind value)) return this.ToFailure(CatalogResult.Failure<object>("kind", "Unknown type."));
            parsed = value;
        }
        return Ok(EntryPageResponse.From(await settlement.GetEntriesAsync(new EntryQuery(vendorId, parsed, null, null, search, page, pageSize), cancellationToken)));
    }

    [HttpPost("adjustments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(AdjustmentBody body, CancellationToken cancellationToken)
    {
        var result = await settlement.AdjustAsync(new NewAdjustment(body.VendorId, body.Amount, body.Reason), AdminId, cancellationToken);
        return result.Succeeded ? StatusCode(StatusCodes.Status201Created, EntryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPost("payouts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePayouts(CreatePayoutsBody body, CancellationToken cancellationToken)
    {
        var result = await settlement.CreatePayoutsAsync(body.CutoffUtc?.ToUniversalTime() ?? DateTime.UtcNow, AdminId, cancellationToken);
        return result.Succeeded
            ? Ok(new PayoutRunResponse(result.Value!.Created.Select(PayoutResponse.From).ToList(), result.Value.Skipped))
            : this.ToFailure(result);
    }

    [HttpGet("payouts")]
    public async Task<IActionResult> Payouts([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        PayoutStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!SettlementRules.TryParse(status, out PayoutStatus value)) return this.ToFailure(CatalogResult.Failure<object>("status", "Unknown status."));
            parsed = value;
        }
        return Ok(PayoutPageResponse.From(await settlement.GetPayoutsAsync(new SettlementCaller(true, AdminId, null), parsed, page, pageSize, cancellationToken)));
    }

    [HttpGet("payouts/export.csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken cancellationToken) =>
        this.Csv(await settlement.ExportPendingPayoutsCsvAsync(AdminId, cancellationToken), "payouts-pending.csv");

    [HttpGet("payouts/{id:int}")]
    public async Task<IActionResult> Payout(int id, CancellationToken cancellationToken) =>
        this.Respond(await settlement.GetPayoutAsync(new SettlementCaller(true, AdminId, null), id, cancellationToken), PayoutDetailResponse.From);

    [HttpPost("payouts/{id:int}/paid")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(int id, MarkPaidBody body, CancellationToken cancellationToken) =>
        this.Respond(await settlement.MarkPaidAsync(id, body.Reference, AdminId, cancellationToken), PayoutResponse.From);

    [HttpPost("payouts/{id:int}/failed")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkFailed(int id, MarkFailedBody body, CancellationToken cancellationToken) =>
        this.Respond(await settlement.MarkFailedAsync(id, body.Reason, AdminId, cancellationToken), PayoutResponse.From);
}
