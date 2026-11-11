using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;

namespace CSSApps.Users.Infrastructure.Data;

public static class SeedDbExtensionMethod
{
    public static void Seed(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Role>().HasData(UsersSeedData.RoleRows);
    }
}

/// <summary>
/// Well-known IDs for service account users.
/// These are used in both database seeding and API key configuration.
/// </summary>
public static class ServiceAccountIds
{
    /// <summary>
    /// Reserved integration service account for a future explicitly authorized integration.
    /// </summary>
    public static readonly Guid IntegrationServiceAccount = Guid.Parse("00000000-0000-0000-0001-000000000002");
}
