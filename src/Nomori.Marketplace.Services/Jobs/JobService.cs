using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Jobs;

/// <summary>
/// Runs the jobs. The database decides who may run one (a lease taken in a single statement), so any number of nodes can call this at the
/// same time. Whatever a job does, the lease is released and the run is recorded.
/// </summary>
public sealed partial class JobService(
    IJobStore store,
    IEnumerable<IScheduledJob> jobs,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<JobOptions> options,
    ILogger<JobService> logger) : IJobService
{
    private readonly Dictionary<string, IScheduledJob> byName = jobs.ToDictionary(j => j.Name, StringComparer.Ordinal);

    public async Task<IReadOnlyList<JobView>> GetJobsAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        var now = clock.UtcNow;
        var rows = (await store.ListAsync(cancellationToken)).ToDictionary(r => r.SystemName, StringComparer.Ordinal);
        return byName.Values.OrderBy(j => j.Name, StringComparer.Ordinal)
            .Where(j => rows.ContainsKey(j.Name))
            .Select(j => ViewOf(j, rows[j.Name], now))
            .ToList();
    }

    public async Task<CatalogResult<JobView>> UpdateAsync(string name, bool enabled, int intervalMinutes, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (!byName.TryGetValue(name, out var job)) return CatalogResult.Error<JobView>(CatalogErrors.NotFound);
        if (!JobRules.IsValidInterval(intervalMinutes))
            return CatalogResult.Failure<JobView>("intervalMinutes", $"The interval must be between {JobRules.MinIntervalMinutes} and {JobRules.MaxIntervalMinutes} minutes.");

        await EnsureAsync(cancellationToken);
        var now = clock.UtcNow;
        if (!await store.UpdateAsync(name, enabled, intervalMinutes, now, cancellationToken)) return CatalogResult.Error<JobView>(CatalogErrors.NotFound);

        await auditLog.WriteAsync("job.updated", actorCustomerId > 0 ? actorCustomerId : null, entityType: "ScheduledTask",
            details: new { job = name, enabled, intervalMinutes }, cancellationToken: cancellationToken);
        var row = (await store.GetAsync(name, cancellationToken))!;
        return CatalogResult.Success(ViewOf(job, row, now));
    }

    public async Task<CatalogResult<ScheduledTaskRun>> RunNowAsync(string name, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (!byName.TryGetValue(name, out var job)) return CatalogResult.Error<ScheduledTaskRun>(CatalogErrors.NotFound);

        await EnsureAsync(cancellationToken);
        var run = await ExecuteAsync(job, JobTriggers.Manual, force: true, cancellationToken);
        if (run is null) return CatalogResult.Error<ScheduledTaskRun>(JobErrors.AlreadyRunning);

        await auditLog.WriteAsync("job.run_manual", actorCustomerId > 0 ? actorCustomerId : null, entityType: "ScheduledTask",
            details: new { job = name, run.Status, run.Processed, run.Failed }, cancellationToken: cancellationToken);
        return CatalogResult.Success(run);
    }

    public async Task<CatalogResult<IReadOnlyList<ScheduledTaskRun>>> GetRunsAsync(string name, CancellationToken cancellationToken) =>
        byName.ContainsKey(name)
            ? CatalogResult.Success(await store.GetRunsAsync(name, JobRules.MaxRunsShown, cancellationToken))
            : CatalogResult.Error<IReadOnlyList<ScheduledTaskRun>>(CatalogErrors.NotFound);

    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        var ran = 0;
        foreach (var name in await store.DueNamesAsync(clock.UtcNow, cancellationToken))
        {
            // A row whose job no longer exists in the code is ignored.
            if (!byName.TryGetValue(name, out var job)) continue;
            if (await ExecuteAsync(job, JobTriggers.Schedule, force: false, cancellationToken) is not null) ran++;
        }
        return ran;
    }

    /// <summary>Null when the lease could not be taken (someone else has the job, or it is not due).</summary>
    private async Task<ScheduledTaskRun?> ExecuteAsync(IScheduledJob job, string trigger, bool force, CancellationToken cancellationToken)
    {
        var owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        var started = clock.UtcNow;
        var lease = started.AddMinutes(Math.Max(1, options.Value.LeaseMinutes));
        if (!await store.TryClaimAsync(job.Name, owner, started, lease, force, cancellationToken)) return null;

        JobResult result;
        string? message;
        string status;
        var interrupted = false;
        try
        {
            result = await job.RunAsync(cancellationToken);
            status = JobRules.StatusOf(result);
            message = JobRules.Trim(result.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The application is stopping. Give the job back, then let the stop go on.
            result = new JobResult(0);
            status = JobStatuses.Failed;
            message = "Stopped before it finished.";
            interrupted = true;
        }
        catch (Exception exception)
        {
            // The log has the full error; the table only gets a short note, never a stack trace.
            LogJobFailed(exception, job.Name);
            result = new JobResult(0, 1);
            status = JobStatuses.Failed;
            message = JobRules.DescribeFailure(exception);
        }

        // The lease must be given back even when the application is stopping, so these writes do not take the caller's token.
        var finished = clock.UtcNow;
        var intervalMinutes = (await store.GetAsync(job.Name, CancellationToken.None))?.IntervalMinutes ?? job.DefaultIntervalMinutes;
        var run = new ScheduledTaskRun
        {
            TaskName = job.Name, Trigger = trigger, StartedUtc = started, FinishedUtc = finished,
            Status = status, Processed = result.Processed, Failed = result.Failed, Message = message
        };
        await store.AddRunAsync(run, CancellationToken.None);
        await store.CompleteAsync(job.Name, owner, new JobFinish(finished, JobRules.NextRun(finished, intervalMinutes), status, message), CancellationToken.None);

        if (interrupted) cancellationToken.ThrowIfCancellationRequested();
        return run;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Job} failed.")]
    private partial void LogJobFailed(Exception exception, string job);

    private Task EnsureAsync(CancellationToken cancellationToken) =>
        store.EnsureAsync(byName.Values.Select(j => new JobDefinition(j.Name, j.DefaultIntervalMinutes)).ToList(), clock.UtcNow, cancellationToken);

    private static JobView ViewOf(IScheduledJob job, ScheduledTask row, DateTime nowUtc) =>
        new(job.Name, job.Description, row.Enabled, row.IntervalMinutes, job.DefaultIntervalMinutes, row.NextRunUtc,
            row.LastStartedUtc, row.LastFinishedUtc, row.LastStatus, row.LastMessage, row.LockedUntilUtc > nowUtc);
}
