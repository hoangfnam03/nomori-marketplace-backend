using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Returns;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Returns;

public sealed record ReturnLineResponse(int Id, int OrderLineId, string Name, string? VariantLabel, int Quantity, decimal Amount)
{
    public static ReturnLineResponse From(ReturnLine line) => new(line.Id, line.OrderLineId, line.Name, line.VariantLabel, line.Quantity, line.Amount);
}

public sealed record ReturnResponse(
    int Id, string Number, int ShopOrderId, int OrderId, string ShopOrderNumber, int VendorId, string ShopName, string Status, string Reason,
    string? CustomerNote, string? ResolutionNote, string CurrencyCode, decimal RefundAmount, bool Restocked, DateTime CreatedOnUtc, DateTime UpdatedOnUtc,
    IReadOnlyList<ReturnLineResponse> Lines)
{
    public static ReturnResponse From(ReturnRequest r) => new(
        r.Id, r.Number, r.ShopOrderId, r.OrderId, r.ShopOrderNumber, r.VendorId, r.ShopName, ReturnRules.ToWire(r.Status), r.Reason,
        r.CustomerNote, r.ResolutionNote, r.CurrencyCode, r.RefundAmount, r.Restocked, r.CreatedOnUtc, r.UpdatedOnUtc,
        r.Lines.Select(ReturnLineResponse.From).ToList());
}

public sealed record ReturnPageResponse(IReadOnlyList<ReturnResponse> Items, int TotalCount, int Page, int PageSize, int TotalPages)
{
    public static ReturnPageResponse From(PagedResult<ReturnRequest> page) =>
        new(page.Items.Select(ReturnResponse.From).ToList(), page.TotalCount, page.Page, page.PageSize, page.TotalPages);
}

public sealed record ReturnLineBody(int OrderLineId, int Quantity);

public sealed record RequestReturnBody(int ShopOrderId, string? Reason, string? Note, IReadOnlyList<ReturnLineBody>? Lines);

public sealed record ReturnNoteBody(string? Note);

public sealed record ReceiveReturnBody(bool Restock);

public sealed record RefundReturnBody(bool Manual);

internal static class ReturnMapping
{
    public static bool TryStatus(string? status, out ReturnStatus? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(status)) return true;
        if (!ReturnRules.TryParseWire(status, out var value)) return false;
        parsed = value;
        return true;
    }

    public static IActionResult Respond(this ControllerBase controller, CatalogResult<ReturnRequest> result) =>
        result.Succeeded ? controller.Ok(ReturnResponse.From(result.Value!)) : controller.ToFailure(result);
}

/// <summary>The returns of the signed-in customer. The customer always comes from the session; someone else's return is 404.</summary>
[ApiController]
[Route("api/v1/returns")]
[Authorize]
public sealed class CustomerReturnsController(IReturnService returns, ICurrentUser currentUser) : ControllerBase
{
    private int CustomerId => int.TryParse(currentUser.Subject, out var id) ? id : 0;

    [HttpGet("reasons")]
    public IActionResult Reasons() => Ok(ReturnReasons.All);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken cancellationToken = default) =>
        Ok(ReturnPageResponse.From(await returns.GetMineAsync(CustomerId, page, pageSize, cancellationToken)));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken) =>
        this.Respond(await returns.GetMineAsync(CustomerId, id, cancellationToken));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RequestReturnBody body, CancellationToken cancellationToken)
    {
        var result = await returns.RequestAsync(CustomerId, new RequestReturnCommand(
            body.ShopOrderId, body.Reason, body.Note, body.Lines?.Select(l => new ReturnLineRequest(l.OrderLineId, l.Quantity)).ToList()), cancellationToken);
        return result.Succeeded
            ? CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, ReturnResponse.From(result.Value))
            : this.ToFailure(result);
    }

    [HttpPost("{id:int}/withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id, CancellationToken cancellationToken) =>
        this.Respond(await returns.WithdrawAsync(CustomerId, id, cancellationToken));
}

/// <summary>The returns of one shop, for members of that shop. Anyone else gets 404, and so does a return of another shop.</summary>
[ApiController]
[Route("api/v1/vendors/{vendorId:int}/returns")]
[Authorize]
public sealed class VendorReturnsController(IReturnService returns, IVendorAccessContext accessContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        int vendorId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (await CallerAsync(vendorId, cancellationToken) is not { } caller) return NotFound();
        if (!ReturnMapping.TryStatus(status, out var parsed)) return this.ToFailure(CatalogResult.Failure<object>("status", "Unknown status."));
        return Ok(ReturnPageResponse.From(await returns.GetAsync(caller, parsed, page, pageSize, cancellationToken)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int vendorId, int id, CancellationToken cancellationToken) =>
        await CallerAsync(vendorId, cancellationToken) is { } caller ? this.Respond(await returns.GetAsync(caller, id, cancellationToken)) : NotFound();

    [HttpPost("{id:int}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int vendorId, int id, ReturnNoteBody body, CancellationToken cancellationToken) =>
        await CallerAsync(vendorId, cancellationToken) is { } caller ? this.Respond(await returns.ApproveAsync(caller, id, body.Note, cancellationToken)) : NotFound();

    [HttpPost("{id:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int vendorId, int id, ReturnNoteBody body, CancellationToken cancellationToken) =>
        await CallerAsync(vendorId, cancellationToken) is { } caller ? this.Respond(await returns.RejectAsync(caller, id, body.Note, cancellationToken)) : NotFound();

    [HttpPost("{id:int}/receive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int vendorId, int id, ReceiveReturnBody body, CancellationToken cancellationToken) =>
        await CallerAsync(vendorId, cancellationToken) is { } caller ? this.Respond(await returns.ReceiveAsync(caller, id, body.Restock, cancellationToken)) : NotFound();

    private async Task<ReturnCaller?> CallerAsync(int vendorId, CancellationToken cancellationToken)
    {
        var caller = await accessContext.GetCallerAsync(cancellationToken);
        return caller.IsAuthenticated && caller.IsMemberOf(vendorId)
            ? new ReturnCaller(OrderActor.Shop, caller.CustomerId!.Value, vendorId)
            : null;
    }
}

/// <summary>Every return of the platform, for administrators who may manage orders.</summary>
[ApiController]
[Route("api/v1/admin/returns")]
[Authorize]
[HasPermission(PermissionCodes.OrdersManage)]
public sealed class AdminReturnsController(IReturnService returns, ICurrentUser currentUser) : ControllerBase
{
    private ReturnCaller Caller => new(OrderActor.Admin, int.TryParse(currentUser.Subject, out var id) ? id : 0, null);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!ReturnMapping.TryStatus(status, out var parsed)) return this.ToFailure(CatalogResult.Failure<object>("status", "Unknown status."));
        return Ok(ReturnPageResponse.From(await returns.GetAsync(Caller, parsed, page, pageSize, cancellationToken)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken) =>
        this.Respond(await returns.GetAsync(Caller, id, cancellationToken));

    [HttpPost("{id:int}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, ReturnNoteBody body, CancellationToken cancellationToken) =>
        this.Respond(await returns.ApproveAsync(Caller, id, body.Note, cancellationToken));

    [HttpPost("{id:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, ReturnNoteBody body, CancellationToken cancellationToken) =>
        this.Respond(await returns.RejectAsync(Caller, id, body.Note, cancellationToken));

    [HttpPost("{id:int}/receive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int id, ReceiveReturnBody body, CancellationToken cancellationToken) =>
        this.Respond(await returns.ReceiveAsync(Caller, id, body.Restock, cancellationToken));

    [HttpPost("{id:int}/refund")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(int id, RefundReturnBody body, CancellationToken cancellationToken) =>
        this.Respond(await returns.RefundAsync(Caller, id, body.Manual, cancellationToken));
}
