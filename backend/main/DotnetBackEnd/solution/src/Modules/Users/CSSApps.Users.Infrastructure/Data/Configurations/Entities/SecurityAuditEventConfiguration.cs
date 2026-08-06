using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSSApps.Users.Infrastructure.Data.Configurations.Entities;

internal sealed class SecurityAuditEventConfiguration : IEntityTypeConfiguration<SecurityAuditEvent>
{
    public void Configure(EntityTypeBuilder<SecurityAuditEvent> builder)
    {
        builder.ToTable("security_audit_events");
        builder.HasKey(auditEvent => auditEvent.Id);
        builder.Property(auditEvent => auditEvent.EventType).HasMaxLength(80).IsRequired();
        builder.Property(auditEvent => auditEvent.Outcome).HasMaxLength(40).IsRequired();
        builder.HasIndex(auditEvent => auditEvent.OccurredAt);
        builder.HasIndex(auditEvent => new { auditEvent.TargetUserId, auditEvent.OccurredAt });
    }
}
