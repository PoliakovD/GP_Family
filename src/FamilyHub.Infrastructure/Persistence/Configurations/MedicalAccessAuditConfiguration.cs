using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicalAccessAuditConfiguration : IEntityTypeConfiguration<MedicalAccessAudit>
{
    public void Configure(EntityTypeBuilder<MedicalAccessAudit> builder)
    {
        builder.ToTable("MedicalAccessAudits", "audit");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();

        // Основные выборки: «кто заходил в мои данные» и «куда заходил пользователь X».
        builder.HasIndex(a => new { a.OwnerUserId, a.OccurredAt });
        builder.HasIndex(a => new { a.ActorUserId, a.OccurredAt });
        // Ретеншн-джоба чистит по дате.
        builder.HasIndex(a => a.OccurredAt);
    }
}
