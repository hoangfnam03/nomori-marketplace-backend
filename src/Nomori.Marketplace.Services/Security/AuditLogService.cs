using System.Text.Json;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Security;

public sealed class AuditLogService(IAuditLogStore auditLogStore, IClock clock) : IAuditLogService
{
    public Task WriteAsync(string eventName, int? customerId = null, int? targetCustomerId = null, string? entityType = null, int? entityId = null, string? ipAddress = null, object? details = null, CancellationToken cancellationToken = default) =>
        auditLogStore.InsertAsync(new AuditLog
        {
            EventName = eventName,
            CustomerId = customerId,
            TargetCustomerId = targetCustomerId,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = ipAddress,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            CreatedOnUtc = clock.UtcNow
        }, cancellationToken);

    public Task<IReadOnlyList<AuditLog>> GetRecentAsync(int take, CancellationToken cancellationToken) =>
        auditLogStore.GetRecentAsync(take, cancellationToken);
}
