namespace Nomori.Marketplace.Core.Time;

/// <summary>
/// Provides the current UTC time for business logic and testability.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}