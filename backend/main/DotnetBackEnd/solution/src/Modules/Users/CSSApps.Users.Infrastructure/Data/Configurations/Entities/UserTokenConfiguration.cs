using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSSApps.Users.Infrastructure.Data.Configurations.Entities;

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> builder)
    {
        builder.ToTable("user_tokens");

        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.TokenHash).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.TokenType, e.IsRevoked });
        builder.HasIndex(e => new { e.SessionId, e.TokenType, e.IsRevoked });
        builder.HasIndex(e => e.Jti).IsUnique();
        builder.HasIndex(e => e.ExpiresAt);
        builder.HasIndex(e => e.IsRevoked);

        builder.Property(e => e.TokenType).IsRequired().HasMaxLength(50);
        builder.Property(e => e.TokenHash).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Jti).HasMaxLength(50); // JWT ID claim
        builder.Property(e => e.ExpiresAt).IsRequired();
        builder.Property(e => e.Provider).IsRequired(false).HasMaxLength(50);
        builder.Property(e => e.SessionId).IsRequired(false);
        builder.Property(e => e.IsRevoked).IsRequired().HasDefaultValue(false);

        // Foreign key to User
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Foreign key to UserSession
        builder.HasOne<UserSession>()
            .WithMany()
            .HasForeignKey(e => e.SessionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
