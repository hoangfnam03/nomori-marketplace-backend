using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Jobs;

/// <summary>Settings of the runner and of the clean-up jobs (section <c>Jobs</c>).</summary>
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>False keeps this node from running scheduled jobs. Manual runs still work.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the runner asks the database what is due.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>How long a started job keeps its lock. A job that dies frees itself after this.</summary>
    public int LeaseMinutes { get; set; } = 10;

    /// <summary>An order that still awaits its payment after this long is cancelled.</summary>
    public int AwaitingPaymentMinutes { get; set; } = 60;

    /// <summary>A payment of a cancelled order is voided only after this long, so a gateway session left open can still report.</summary>
    public int StalePaymentGraceHours { get; set; } = 24;

    public int ReservationRetentionDays { get; set; } = 30;
    public int CartRetentionDays { get; set; } = 90;
    public int RunRetentionDays { get; set; } = 30;
}

/// <summary>Business-rule codes of jobs (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class JobErrors
{
    public const string AlreadyRunning = "job.already_running";
}

public static class JobTriggers
{
    public const string Schedule = "schedule";
    public const string Manual = "manual";
}

public static class JobStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
}

/// <summary>What a job did: how many items it dealt with, how many it could not, and a short note.</summary>
public sealed record JobResult(int Processed, int Failed = 0, string? Message = null);

/// <summary>
/// One background task. It has to be idempotent and work in small batches (<see cref="JobRules.BatchSize"/>): the runner may run it again
/// after a crash, and it never runs twice at the same time.
/// </summary>
public interface IScheduledJob
{
    /// <summary>Stable, lower-case, dot separated. Part of the database contract: never rename.</summary>
    string Name { get; }

    string Description { get; }
    int DefaultIntervalMinutes { get; }

    Task<JobResult> RunAsync(CancellationToken cancellationToken);
}

public sealed class ScheduledTask
{
    public string SystemName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastStartedUtc { get; set; }
    public DateTime? LastFinishedUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastMessage { get; set; }
}

public sealed class ScheduledTaskRun
{
    public long Id { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Processed { get; set; }
    public int Failed { get; set; }
    public string? Message { get; set; }
}

/// <summary>A job as an administrator sees it: what the code says plus what the database holds.</summary>
public sealed record JobView(
    string Name, string Description, bool Enabled, int IntervalMinutes, int DefaultIntervalMinutes, DateTime? NextRunUtc,
    DateTime? LastStartedUtc, DateTime? LastFinishedUtc, string? LastStatus, string? LastMessage, bool Running);

public sealed record JobDefinition(string Name, int DefaultIntervalMinutes);

/// <summary>How a run ended, for <see cref="IJobStore.CompleteAsync"/>.</summary>
public sealed record JobFinish(DateTime FinishedUtc, DateTime NextRunUtc, string Status, string? Message);

public static class JobRules
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 10_080;
    public const int BatchSize = 200;
    public const int MaxMessageLength = 500;
    public const int MaxRunsShown = 50;

    public static bool IsValidInterval(int minutes) => minutes is >= MinIntervalMinutes and <= MaxIntervalMinutes;

    /// <summary>The status a finished run gets: all items done, some failed, or nothing worked and something failed.</summary>
    public static string StatusOf(JobResult result) =>
        result.Failed == 0 ? JobStatuses.Succeeded : result.Processed > 0 ? JobStatuses.Partial : JobStatuses.Failed;

    public static DateTime NextRun(DateTime finishedUtc, int intervalMinutes) => finishedUtc.AddMinutes(intervalMinutes);

    /// <summary>A note short enough for the table. A job's note is its own; an exception's note is its type and message (never a stack trace).</summary>
    public static string? Trim(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var single = message.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxMessageLength ? single : single[..(MaxMessageLength - 1)] + "…";
    }

    public static string DescribeFailure(Exception exception) => Trim($"{exception.GetType().Name}: {exception.Message}")!;
}

/// <summary>Schedules, locks and run history. Everything that has to be atomic is one statement.</summary>
public interface IJobStore
{
    /// <summary>Adds a row for every definition that has none yet. Existing rows (and their settings) are left alone.</summary>
    Task EnsureAsync(IReadOnlyCollection<JobDefinition> definitions, DateTime nowUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken);
    Task<ScheduledTask?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Returns false when there is no such job.</summary>
    Task<bool> UpdateAsync(string name, bool enabled, int intervalMinutes, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Names of the jobs that are enabled, due and not locked.</summary>
    Task<IReadOnlyList<string>> DueNamesAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the lock in one statement: only while nobody holds an unexpired lease and (unless <paramref name="force"/>) the job is enabled and due.
    /// False means someone else has it, or it is not due.
    /// </summary>
    Task<bool> TryClaimAsync(string name, string owner, DateTime nowUtc, DateTime leaseUntilUtc, bool force, CancellationToken cancellationToken);

    /// <summary>Releases the lock and records how the run ended and when the next one is due. Only the owner can do it.</summary>
    Task CompleteAsync(string name, string owner, JobFinish finish, CancellationToken cancellationToken);

    Task AddRunAsync(ScheduledTaskRun run, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScheduledTaskRun>> GetRunsAsync(string name, int take, CancellationToken cancellationToken);
}

/// <summary>
/// The queries and deletes of the clean-up jobs. They read other modules' tables on purpose and in one place, so no module's store has to
/// know about jobs; the changes themselves go through the owning services.
/// </summary>
public interface IMaintenanceStore
{
    /// <summary>Orders still awaiting payment, made before the cut-off, with at least one pending shop order. Oldest first.</summary>
    Task<IReadOnlyList<int>> ExpiredAwaitingOrderIdsAsync(DateTime createdBeforeUtc, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<int>> PaymentIdsForOrderAsync(int orderId, CancellationToken cancellationToken);

    /// <summary>Pending or authorized payments of orders whose shop orders are all cancelled, last changed before the cut-off. Oldest first.</summary>
    Task<IReadOnlyList<int>> StalePaymentIdsAsync(DateTime changedBeforeUtc, int take, CancellationToken cancellationToken);

    /// <summary>Closes active stock holds that expired. Returns how many.</summary>
    Task<int> CloseExpiredReservationsAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Deletes closed (committed or released) holds last changed before the cut-off. Returns how many.</summary>
    Task<int> DeleteClosedReservationsAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken);

    Task<int> DeleteStaleCartLinesAsync(DateTime untouchedSinceUtc, int take, CancellationToken cancellationToken);
    Task<int> DeleteRunsAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken);
}

public interface IJobService
{
    Task<IReadOnlyList<JobView>> GetJobsAsync(CancellationToken cancellationToken);

    Task<CatalogResult<JobView>> UpdateAsync(string name, bool enabled, int intervalMinutes, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>Runs the job now, even when it is off or not due. <see cref="JobErrors.AlreadyRunning"/> while it holds its lock.</summary>
    Task<CatalogResult<ScheduledTaskRun>> RunNowAsync(string name, int actorCustomerId, CancellationToken cancellationToken);

    Task<CatalogResult<IReadOnlyList<ScheduledTaskRun>>> GetRunsAsync(string name, CancellationToken cancellationToken);

    /// <summary>Makes sure every job has a row, then runs the ones that are due, one after the other. Returns how many ran.</summary>
    Task<int> RunDueAsync(CancellationToken cancellationToken);
}
