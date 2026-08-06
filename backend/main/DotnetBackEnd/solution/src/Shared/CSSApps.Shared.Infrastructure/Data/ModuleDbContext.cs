using CSSApps.Shared.Contracts.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSSApps.Shared.Infrastructure.Data;

public abstract class ModuleDbContext<TDbContext>(DbContextOptions<TDbContext> options)
    : DbContext(options), IModuleDbContext
    where TDbContext : DbContext
{
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        return base.SaveChanges();
    }

    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        return await Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitTransactionAsync()
    {
        var transaction = Database.CurrentTransaction;
        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }
    }

    public async Task RollbackTransactionAsync()
    {
        var transaction = Database.CurrentTransaction;
        if (transaction is not null)
        {
            await transaction.RollbackAsync();
        }
    }
}