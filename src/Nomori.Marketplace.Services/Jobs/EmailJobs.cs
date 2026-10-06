using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Jobs;

/// <summary>
/// Sends the queued emails. A failed send is tried again later with a growing wait, up to <see cref="EmailOptions.QueueMaxAttempts"/>; then the
/// email stays failed until an administrator retries it. While delivery is switched off nothing is taken, so mail queued meanwhile goes out
/// when it is switched on.
/// </summary>
public sealed partial class SendQueuedEmailsJob(
    IEmailQueueStore store,
    IEmailSender sender,
    IClock clock,
    IOptions<EmailOptions> options,
    ILogger<SendQueuedEmailsJob> logger) : IScheduledJob
{
    public string Name => "email.send_queued";
    public string Description => "Sends queued emails and tries again later when sending fails.";
    public int DefaultIntervalMinutes => 1;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return new JobResult(0, 0, "Email delivery is disabled; queued emails wait.");

        var now = clock.UtcNow;
        var batch = await store.ClaimDueAsync(now, now.AddMinutes(Math.Max(1, settings.QueueLeaseMinutes)), EmailQueueRules.BatchSize, cancellationToken);
        var sent = 0;
        var failed = 0;
        foreach (var email in batch)
        {
            try
            {
                await sender.SendEmailAsync(new EmailMessage(email.ToAddress, email.Subject, email.HtmlBody, email.TextBody), cancellationToken);
                await store.MarkSentAsync(email.Id, clock.UtcNow, cancellationToken);
                sent++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The app is stopping: the lease frees the email for the next run.
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                LogSendFailed(ex, email.Id, email.Attempts);
                var giveUp = email.Attempts >= Math.Max(1, settings.QueueMaxAttempts);
                await store.MarkAttemptFailedAsync(email.Id, EmailQueueRules.DescribeFailure(ex),
                    giveUp ? null : EmailQueueRules.NextAttempt(clock.UtcNow, email.Attempts), cancellationToken);
            }
        }
        return new JobResult(sent, failed, batch.Count == 0 ? null : $"Sent {sent}, failed {failed}.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending queued email {EmailId} failed (attempt {Attempts}).")]
    private partial void LogSendFailed(Exception exception, long emailId, int attempts);
}

/// <summary>Deletes old sent and failed emails. Pending ones are never deleted by a job.</summary>
public sealed class PurgeEmailQueueJob(IEmailQueueStore store, IClock clock, IOptions<EmailOptions> options) : IScheduledJob
{
    public string Name => "email.purge_queue";
    public string Description => "Deletes sent and failed emails older than the retention period.";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var deleted = await store.DeleteOldAsync(
            now.AddDays(-Math.Max(1, options.Value.SentRetentionDays)), now.AddDays(-Math.Max(1, options.Value.FailedRetentionDays)),
            JobRules.BatchSize * 5, cancellationToken);
        return new JobResult(deleted, 0, deleted == 0 ? null : $"Deleted {deleted} old emails.");
    }
}
