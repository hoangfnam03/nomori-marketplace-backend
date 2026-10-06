using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Email;

public sealed record QueuedEmailSummaryResponse(
    long Id, string Kind, string ToAddress, string Subject, string Status, int Attempts, DateTime NextAttemptUtc,
    string? LastError, DateTime CreatedOnUtc, DateTime? SentOnUtc)
{
    public static QueuedEmailSummaryResponse From(QueuedEmail email) => new(
        email.Id, email.Kind, email.ToAddress, email.Subject, email.Status, email.Attempts, email.NextAttemptUtc,
        email.LastError, email.CreatedOnUtc, email.SentOnUtc);
}

public sealed record QueuedEmailDetailResponse(
    long Id, string Kind, string ToAddress, string Subject, string Status, int Attempts, DateTime NextAttemptUtc,
    string? LastError, DateTime CreatedOnUtc, DateTime? SentOnUtc, string HtmlBody, string? TextBody)
{
    public static QueuedEmailDetailResponse From(QueuedEmail email) => new(
        email.Id, email.Kind, email.ToAddress, email.Subject, email.Status, email.Attempts, email.NextAttemptUtc,
        email.LastError, email.CreatedOnUtc, email.SentOnUtc, email.HtmlBody, email.TextBody);
}

public sealed record QueuedEmailPageResponse(
    IReadOnlyList<QueuedEmailSummaryResponse> Items, int TotalCount, int Page, int PageSize, int TotalPages,
    IReadOnlyDictionary<string, int> Counts);

[ApiController]
[Route("api/v1/admin/emails")]
[Authorize]
[HasPermission(PermissionCodes.EmailsManage)]
public sealed class AdminEmailsController(IEmailQueueService emailQueue, ICurrentUser currentUser) : ControllerBase
{
    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await emailQueue.ListAsync(new EmailQueueQuery(status, search, page, pageSize), cancellationToken);
        var counts = await emailQueue.GetCountsAsync(cancellationToken);
        return Ok(new QueuedEmailPageResponse(
            result.Items.Select(QueuedEmailSummaryResponse.From).ToList(), result.TotalCount, result.Page, result.PageSize, result.TotalPages, counts));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var result = await emailQueue.GetAsync(id, cancellationToken);
        return result.Succeeded ? Ok(QueuedEmailDetailResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPost("{id:long}/retry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(long id, CancellationToken cancellationToken)
    {
        var result = await emailQueue.RetryAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(QueuedEmailSummaryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("{id:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await emailQueue.DeleteAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }
}
