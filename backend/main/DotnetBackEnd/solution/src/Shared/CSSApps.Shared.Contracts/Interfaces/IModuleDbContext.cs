using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSSApps.Shared.Contracts.Interfaces;

public interface IModuleDbContext
{
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
    DbSet<TEntity> Set<TEntity>() where TEntity : class;
    EntityEntry<TEntity> Add<TEntity>(TEntity entity) where TEntity : class;
    ValueTask<EntityEntry<TEntity>> AddAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : class;
    EntityEntry<TEntity> Update<TEntity>(TEntity entity) where TEntity : class;
    EntityEntry<TEntity> Remove<TEntity>(TEntity entity) where TEntity : class;
    EntityEntry<TEntity> Attach<TEntity>(TEntity entity) where TEntity : class;

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    int SaveChanges();
}