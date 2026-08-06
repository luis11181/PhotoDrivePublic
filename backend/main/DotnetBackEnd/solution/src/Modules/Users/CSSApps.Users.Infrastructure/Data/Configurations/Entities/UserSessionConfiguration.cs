using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSSApps.Users.Infrastructure.Data.Configurations.Entities;

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");

        builder.HasKey(e => e.Id);

        // Indexes for performance
        builder.HasIndex(e => new { e.UserId, e.IsActive, e.LastActivity });

        // Properties
        builder.Property(e => e.DeviceName).HasMaxLength(100);
        builder.Property(e => e.IpAddress).IsRequired().HasMaxLength(45); // IPv6 max length
        builder.Property(e => e.UserAgent).IsRequired().HasMaxLength(1000);
        builder.Property(e => e.LastActivity).IsRequired();
        builder.Property(e => e.FirstLoginAt).IsRequired();
        builder.Property(e => e.LoginCount).IsRequired().HasDefaultValue(1);
        builder.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(e => e.TerminatedAt).IsRequired(false);
        builder.Property(e => e.TerminationReason).HasMaxLength(200);

        // Foreign key to User
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
