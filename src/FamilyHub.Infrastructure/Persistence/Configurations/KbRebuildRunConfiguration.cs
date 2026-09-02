using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class KbRebuildRunConfiguration : IEntityTypeConfiguration<KbRebuildRun>
{
    public void Configure(EntityTypeBuilder<KbRebuildRun> builder)
    {
        // public — инфраструктурное состояние (как EncryptionRotationRun/OutboxMessage), не медданные.
        builder.ToTable("KbRebuildRuns");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.LastError).HasMaxLength(2000);

        // Не более одного активного прогона одновременно (Status=Running=0) — второй клик
        // "Пересобрать справочник" видит уже идущий прогон и присоединяется к нему вместо старта
        // нового (см. AdminKbRebuildService), тот же приём, что EncryptionRotationRunConfiguration.
        builder.HasIndex(r => r.Status)
            .IsUnique()
            .HasFilter("\"Status\" = 0");

        builder.HasIndex(r => r.StartedAt);
    }
}
