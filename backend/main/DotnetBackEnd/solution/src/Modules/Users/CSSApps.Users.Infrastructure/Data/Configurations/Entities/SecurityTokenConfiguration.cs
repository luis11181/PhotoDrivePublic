using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSSApps.Users.Infrastructure.Data.Configurations.Entities;

internal sealed class SecurityTokenConfiguration : IEntityTypeConfiguration<SecurityToken>
{
    public void Configure(EntityTypeBuilder<SecurityToken> builder)
    {
        builder.ToTable("security_tokens");
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Purpose).HasConversion<string>().HasMaxLength(40);
        builder.Property(token => token.SecretHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(token => token.SecretHash).IsUnique();
        builder.HasIndex(token => new { token.UserId, token.Purpose, token.UsedAt, token.ExpiresAt });
        builder.HasOne<User>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
