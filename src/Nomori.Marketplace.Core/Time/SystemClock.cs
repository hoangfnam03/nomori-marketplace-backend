namespace Nomori.Marketplace.Core.Time;

/// <summary>
/// Reads the current UTC time from the system clock.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}