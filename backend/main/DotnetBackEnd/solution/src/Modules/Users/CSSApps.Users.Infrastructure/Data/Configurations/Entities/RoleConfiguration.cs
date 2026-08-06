using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSSApps.Users.Infrastructure.Data.Configurations.Entities;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Name).IsUnique();

        // Property configurations
        builder.Property(e => e.Name).IsRequired().HasMaxLength(100);
        builder.Property(e => e.Description).HasMaxLength(500);

        builder.HasMany(r => r.Permissions)
            .WithMany(p => p.Roles)
            .UsingEntity(
                "role_permissions",
                l => l.HasOne(typeof(Permission)).WithMany().HasForeignKey("permission_id"),
                r => r.HasOne(typeof(Role)).WithMany().HasForeignKey("role_id"));
    }
}