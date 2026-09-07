namespace Nomori.Marketplace.Core.Events;

/// <summary>
/// Marks an event raised by a domain operation.
/// </summary>
public interface IDomainEvent
{
    DateTime OccurredOnUtc { get; }
}