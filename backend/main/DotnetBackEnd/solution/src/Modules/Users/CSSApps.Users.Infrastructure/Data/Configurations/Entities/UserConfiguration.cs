using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSSApps.Users.Infrastructure.Data.Configurations.Entities;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Email).IsUnique();
        builder.HasIndex(e => new { e.OAuthProvider, e.OAuthProviderId }).IsUnique();

        builder.Property(e => e.OAuthProviderId).IsRequired(false).HasMaxLength(255);
        builder.Property(e => e.OAuthProvider).IsRequired(false).HasMaxLength(50);
        builder.Property(e => e.PasswordHash).IsRequired(false).HasMaxLength(500);
        builder.Property(e => e.AccountStatus)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(HumanAccountStatus.Invited);
        builder.Property(e => e.AllowedLoginMethods).HasConversion<int>();
        builder.Property(e => e.SecurityStamp).IsRequired();
        builder.HasIndex(e => new { e.IsServiceAccount, e.AccountStatus });

        builder.ComplexProperty(u => u.Name, nameBuilder =>
        {
            nameBuilder.IsRequired();
            nameBuilder.Property(n => n.FirstName).IsRequired().HasMaxLength(100);
            nameBuilder.Property(n => n.LastName).IsRequired(false).HasMaxLength(100);
        });

        // Phone as complex type (optional)
        builder.ComplexProperty(u => u.Phone, phoneBuilder =>
        {
            phoneBuilder.IsRequired(false);
            phoneBuilder.Property(p => p.CountryCode).IsRequired().HasMaxLength(5);
            phoneBuilder.Property(p => p.Number).IsRequired().HasMaxLength(20);
        });

        builder.HasMany(u => u.Roles)
            .WithMany(r => r.Users)
            .UsingEntity(
                "user_roles",
                l => l.HasOne(typeof(Role)).WithMany().HasForeignKey("role_id"),
                r => r.HasOne(typeof(User)).WithMany().HasForeignKey("user_id"));
    }
}
