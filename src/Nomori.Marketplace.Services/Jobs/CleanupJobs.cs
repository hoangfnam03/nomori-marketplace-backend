using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Jobs;

/// <summary>
/// Closes stock holds that expired and deletes old closed ones. Availability already ignores an expired hold, so this changes no stock figure:
/// it only keeps the table small and the unique index of active holds free.
/// </summary>
public sealed class PurgeReservationsJob(IMaintenanceStore maintenance, IClock clock, IOptions<JobOptions> options) : IScheduledJob
{
    public string Name => "inventory.purge_reservations";
    public string Description => "Closes expired stock holds and deletes old closed ones.";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var closed = await maintenance.CloseExpiredReservationsAsync(now, cancellationToken);
        var deleted = await maintenance.DeleteClosedReservationsAsync(
            now.AddDays(-Math.Max(1, options.Value.ReservationRetentionDays)), JobRules.BatchSize, cancellationToken);
        return new JobResult(closed + deleted, 0, closed + deleted == 0 ? null : $"Closed {closed} expired holds, deleted {deleted} old ones.");
    }
}

/// <summary>Deletes cart lines nobody has touched for a long time.</summary>
public sealed class PurgeStaleCartsJob(IMaintenanceStore maintenance, IClock clock, IOptions<JobOptions> options) : IScheduledJob
{
    public string Name => "cart.purge_stale";
    public string Description => "Deletes cart lines that were not touched for a long time.";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var deleted = await maintenance.DeleteStaleCartLinesAsync(
            clock.UtcNow.AddDays(-Math.Max(1, options.Value.CartRetentionDays)), JobRules.BatchSize, cancellationToken);
        return new JobResult(deleted, 0, deleted == 0 ? null : $"Deleted {deleted} stale cart lines.");
    }
}

/// <summary>Deletes old run history, so the job tables do not grow for ever.</summary>
public sealed class PurgeJobHistoryJob(IMaintenanceStore maintenance, IClock clock, IOptions<JobOptions> options) : IScheduledJob
{
    public string Name => "jobs.purge_history";
    public string Description => "Deletes job run history older than the retention period.";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var deleted = await maintenance.DeleteRunsAsync(
            clock.UtcNow.AddDays(-Math.Max(1, options.Value.RunRetentionDays)), JobRules.BatchSize * 5, cancellationToken);
        return new JobResult(deleted, 0, deleted == 0 ? null : $"Deleted {deleted} old runs.");
    }
}
