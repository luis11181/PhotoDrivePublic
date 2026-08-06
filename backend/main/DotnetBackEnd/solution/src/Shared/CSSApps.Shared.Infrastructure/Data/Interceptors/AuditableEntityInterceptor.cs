using CSSApps.Shared.Contracts.DomainCommons.Entities;
using CSSApps.Shared.Contracts.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSSApps.Shared.Infrastructure.Data.Interceptors;

/// <summary>
/// Interceptor that automatically populates audit fields (CreatedBy, CreatedDate, UpdatedBy, UpdatedDate)
/// on entities that inherit from AuditableEntity.
/// </summary>
public class AuditableEntityInterceptor : SaveChangesInterceptor
{
    private readonly IExecutionContextAccessor _user;

    public AuditableEntityInterceptor(IExecutionContextAccessor user)
    {
        _user = user;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        UpdateEntities(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        UpdateEntities(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = new())
    {
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        base.SaveChangesFailed(eventData);
    }

    private void UpdateEntities(DbContext? context)
    {
        if (context == null) return;

        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedBy = _user.UserId?.ToString() ?? "System";
                entry.Entity.CreatedDate = DateTime.UtcNow;
            }

            if (entry.State is EntityState.Added or EntityState.Modified ||
                entry.HasChangedOwnedEntities())
            {
                entry.Entity.UpdatedBy = _user.UserId?.ToString() ?? "System";
                entry.Entity.UpdatedDate = DateTime.UtcNow;
            }
        }
    }
}

public static class EntityEntryExtensions
{
    public static bool HasChangedOwnedEntities(this EntityEntry entry)
    {
        return entry.References.Any(r =>
            r.TargetEntry != null &&
            r.TargetEntry.Metadata.IsOwned() &&
            r.TargetEntry.State is EntityState.Added or EntityState.Modified);
    }
}
