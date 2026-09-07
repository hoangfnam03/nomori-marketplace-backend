using Nomori.Marketplace.Core.Domain;

namespace Nomori.Marketplace.Data.Persistence;

/// <summary>
/// Defines the minimum repository boundary for persisted entities.
/// </summary>
public interface IRepository<TEntity> where TEntity : BaseEntity
{
    IQueryable<TEntity> Query();
}