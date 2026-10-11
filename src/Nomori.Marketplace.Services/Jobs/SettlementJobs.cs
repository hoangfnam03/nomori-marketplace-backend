using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Settlement;

namespace Nomori.Marketplace.Services.Jobs;

/// <summary>
/// Writes the ledger lines of completed shop orders and refunded returns that have none yet (F05-S, D6). Idempotent: a unique index per
/// shop order and per return means a second run, or a second node, writes nothing twice.
/// </summary>
public sealed class RecordSettlementEntriesJob(ISettlementService settlement) : IScheduledJob
{
    public string Name => "settlement.record_entries";
    public string Description => "Adds completed shop orders and refunded returns to the ledger of their shop.";
    public int DefaultIntervalMinutes => 5;

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var (recorded, failed) = await settlement.RecordPendingAsync(cancellationToken);
        return new JobResult(recorded, failed);
    }
}
