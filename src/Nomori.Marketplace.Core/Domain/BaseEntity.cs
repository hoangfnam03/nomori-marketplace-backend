namespace Nomori.Marketplace.Core.Domain;

/// <summary>
/// Provides the common identifier for persisted domain entities.
/// </summary>
public abstract class BaseEntity
{
    public int Id { get; set; }
}