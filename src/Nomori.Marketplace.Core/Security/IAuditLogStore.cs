namespace Nomori.Marketplace.Core.Security;

public interface IAuditLogStore
{
    Task InsertAsync(AuditLog entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditLog>> GetRecentAsync(int take, CancellationToken cancellationToken);
}

public interface IAuditLogService
{
    Task WriteAsync(
        string eventName,
        int? customerId = null,
        int? targetCustomerId = null,
        string? entityType = null,
        int? entityId = null,
        string? ipAddress = null,
        object? details = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLog>> GetRecentAsync(int take, CancellationToken cancellationToken);
}
