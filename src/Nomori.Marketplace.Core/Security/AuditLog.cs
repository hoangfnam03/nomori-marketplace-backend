namespace Nomori.Marketplace.Core.Security;

public sealed class AuditLog
{
    public int Id { get; set; }

    public string EventName { get; set; } = string.Empty;

    public int? CustomerId { get; set; }

    public int? TargetCustomerId { get; set; }

    public string? EntityType { get; set; }

    public int? EntityId { get; set; }

    public string? IpAddress { get; set; }

    public string? DetailsJson { get; set; }

    public DateTime CreatedOnUtc { get; set; }
}
