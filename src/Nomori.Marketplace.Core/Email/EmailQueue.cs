using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Email;

public static class QueuedEmailStatuses
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Pending, Sending, Sent, Failed];
}

/// <summary>Business-rule codes of the email queue (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class EmailQueueErrors
{
    /// <summary>Only a failed email can be sent again by hand.</summary>
    public const string NotRetryable = "email.not_retryable";

    /// <summary>An email that is being sent right now cannot be deleted.</summary>
    public const string Busy = "email.busy";
}

public static class EmailKinds
{
    public const string OrderPlaced = "order.placed";
    public const string OrderShipped = "order.shipped";
    public const string OrderDelivered = "order.delivered";
    public const string OrderCancelled = "order.cancelled";
    public const string ReturnApproved = "return.approved";
    public const string ReturnRejected = "return.rejected";
    public const string ReturnRefunded = "return.refunded";
    public const string UnpaidOrderReminder = ReminderKinds.UnpaidOrder;
    public const string AbandonedCartReminder = ReminderKinds.AbandonedCart;
}

public sealed class QueuedEmail
{
    public long Id { get; set; }

    /// <summary>What the email is about (<see cref="EmailKinds"/>): lets an administrator filter, and later lets a template version be chosen.</summary>
    public string Kind { get; set; } = string.Empty;

    public string ToAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string? TextBody { get; set; }
    public string Status { get; set; } = QueuedEmailStatuses.Pending;

    /// <summary>How many times sending was started. Counted when the email is taken, so a crash during sending counts too.</summary>
    public int Attempts { get; set; }

    public DateTime NextAttemptUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime? SentOnUtc { get; set; }
}

public sealed record EmailQueueQuery(string? Status, string? Search, int Page, int PageSize);

public static class EmailQueueRules
{
    public const int MaxKindLength = 50;
    public const int MaxAddressLength = 320;
    public const int MaxSubjectLength = 255;
    public const int MaxErrorLength = 500;
    public const int BatchSize = 50;
    public const int MaxPageSize = 100;

    // The wait after the first, second, ... failure. A mail server that is down for a while is tried again, but not hammered.
    private static readonly int[] BackoffMinutes = [2, 10, 30, 120];

    public static DateTime NextAttempt(DateTime nowUtc, int attempts) =>
        nowUtc.AddMinutes(BackoffMinutes[Math.Clamp(attempts - 1, 0, BackoffMinutes.Length - 1)]);

    public static bool IsValidAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) && address.Trim().Length <= MaxAddressLength
        && System.Net.Mail.MailAddress.TryCreate(address.Trim(), out var parsed) && parsed.Address == address.Trim();

    /// <summary>One line, never a stack trace and never longer than the column.</summary>
    public static string DescribeFailure(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}".ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxErrorLength ? text : text[..(MaxErrorLength - 1)] + "…";
    }
}

/// <summary>Where queued emails live. Everything that has to be atomic is one statement.</summary>
public interface IEmailQueueStore
{
    Task<long> InsertAsync(QueuedEmail email, CancellationToken cancellationToken);

    /// <summary>
    /// Takes up to <paramref name="take"/> emails that are due (pending and not before their time, or sending with an expired lease), marks them
    /// sending until <paramref name="leaseUntilUtc"/> and counts the attempt, in one statement. Two nodes never get the same email.
    /// </summary>
    Task<IReadOnlyList<QueuedEmail>> ClaimDueAsync(DateTime nowUtc, DateTime leaseUntilUtc, int take, CancellationToken cancellationToken);

    Task MarkSentAsync(long id, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Records a failed attempt: back to pending for <paramref name="nextAttemptUtc"/>, or failed for good when it is null.</summary>
    Task MarkAttemptFailedAsync(long id, string failure, DateTime? nextAttemptUtc, CancellationToken cancellationToken);

    /// <summary>Failed to pending, with the attempts counted from zero. False when the email is not failed.</summary>
    Task<bool> RetryAsync(long id, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>False when there is no such email or it is being sent.</summary>
    Task<bool> DeleteAsync(long id, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Deletes sent emails older than one cut-off and failed ones older than the other. Returns how many.</summary>
    Task<int> DeleteOldAsync(DateTime sentBeforeUtc, DateTime failedBeforeUtc, int take, CancellationToken cancellationToken);

    /// <summary>The list never carries the bodies.</summary>
    Task<PagedResult<QueuedEmail>> ListAsync(EmailQueueQuery query, CancellationToken cancellationToken);

    Task<QueuedEmail?> GetAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken cancellationToken);
}

public interface IEmailQueueService
{
    /// <summary>
    /// Puts an email in the queue; a job sends it. A wrong address or an empty subject is refused (nothing is queued), so a bad customer record
    /// cannot fill the queue with mail that can never go. Never put a secret (a reset link, a code) in a queued email: it stays in the table.
    /// </summary>
    Task<CatalogResult<long>> EnqueueAsync(string kind, EmailMessage message, CancellationToken cancellationToken);

    Task<PagedResult<QueuedEmail>> ListAsync(EmailQueueQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, int>> GetCountsAsync(CancellationToken cancellationToken);
    Task<CatalogResult<QueuedEmail>> GetAsync(long id, CancellationToken cancellationToken);
    Task<CatalogResult<QueuedEmail>> RetryAsync(long id, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<bool>> DeleteAsync(long id, int actorCustomerId, CancellationToken cancellationToken);
}
