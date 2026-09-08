using Nomori.Marketplace.Core.Domain;

namespace Nomori.Marketplace.Core.Domain.Customers;

public sealed class CustomerRole : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public string SystemName { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public bool IsSystemRole { get; set; }
}