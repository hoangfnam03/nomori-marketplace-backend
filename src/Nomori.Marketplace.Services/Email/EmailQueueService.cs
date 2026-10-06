using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Email;

public sealed class EmailQueueService(IEmailQueueStore store, IAuditLogService auditLog, IClock clock) : IEmailQueueService
{
    public async Task<CatalogResult<long>> EnqueueAsync(string kind, EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var errors = new Dictionary<string, string[]>();
        var cleanKind = kind?.Trim() ?? string.Empty;
        var to = message.ToAddress?.Trim() ?? string.Empty;
        var subject = message.Subject?.ReplaceLineEndings(" ").Trim() ?? string.Empty;

        if (cleanKind.Length is 0 or > EmailQueueRules.MaxKindLength) errors["kind"] = ["The email needs a kind."];
        if (!EmailQueueRules.IsValidAddress(to)) errors["toAddress"] = ["The address is not a valid email address."];
        if (subject.Length is 0 || string.IsNullOrWhiteSpace(message.HtmlBody)) errors["message"] = ["The email needs a subject and a body."];
        if (errors.Count > 0) return CatalogResult.Failure<long>(errors);

        var now = clock.UtcNow;
        var id = await store.InsertAsync(new QueuedEmail
        {
            Kind = cleanKind, ToAddress = to,
            Subject = subject.Length > EmailQueueRules.MaxSubjectLength ? subject[..EmailQueueRules.MaxSubjectLength] : subject,
            HtmlBody = message.HtmlBody, TextBody = message.TextBody, Status = QueuedEmailStatuses.Pending,
            NextAttemptUtc = now, CreatedOnUtc = now
        }, cancellationToken);
        return CatalogResult.Success(id);
    }

    public Task<PagedResult<QueuedEmail>> ListAsync(EmailQueueQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query with
        {
            Status = QueuedEmailStatuses.All.Contains(query.Status ?? string.Empty) ? query.Status : null,
            Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            Page = Math.Max(query.Page, 1),
            PageSize = Math.Clamp(query.PageSize, 1, EmailQueueRules.MaxPageSize)
        }, cancellationToken);

    public Task<IReadOnlyDictionary<string, int>> GetCountsAsync(CancellationToken cancellationToken) => store.CountByStatusAsync(cancellationToken);

    public async Task<CatalogResult<QueuedEmail>> GetAsync(long id, CancellationToken cancellationToken)
    {
        var email = await store.GetAsync(id, cancellationToken);
        return email is null ? CatalogResult.Error<QueuedEmail>(CatalogErrors.NotFound) : CatalogResult.Success(email);
    }

    public async Task<CatalogResult<QueuedEmail>> RetryAsync(long id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var email = await store.GetAsync(id, cancellationToken);
        if (email is null) return CatalogResult.Error<QueuedEmail>(CatalogErrors.NotFound);
        if (!await store.RetryAsync(id, clock.UtcNow, cancellationToken)) return CatalogResult.Error<QueuedEmail>(EmailQueueErrors.NotRetryable);

        await auditLog.WriteAsync("email.retry", actorCustomerId > 0 ? actorCustomerId : null, entityType: "QueuedEmail",
            details: new { emailId = id, email.Kind }, cancellationToken: cancellationToken);
        return CatalogResult.Success((await store.GetAsync(id, cancellationToken))!);
    }

    public async Task<CatalogResult<bool>> DeleteAsync(long id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var email = await store.GetAsync(id, cancellationToken);
        if (email is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);
        if (!await store.DeleteAsync(id, clock.UtcNow, cancellationToken)) return CatalogResult.Error<bool>(EmailQueueErrors.Busy);

        await auditLog.WriteAsync("email.deleted", actorCustomerId > 0 ? actorCustomerId : null, entityType: "QueuedEmail",
            details: new { emailId = id, email.Kind, email.Status }, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }
}
