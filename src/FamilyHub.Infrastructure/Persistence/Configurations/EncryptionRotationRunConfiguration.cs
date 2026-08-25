using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class EncryptionRotationRunConfiguration : IEntityTypeConfiguration<EncryptionRotationRun>
{
    public void Configure(EntityTypeBuilder<EncryptionRotationRun> builder)
    {
        // public — инфраструктурное состояние (как OutboxMessage/DataProtectionKeys), не ПДн/медданные.
        builder.ToTable("EncryptionRotationRuns");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.TargetKeyId).HasMaxLength(255).IsRequired();
        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.LastError).HasMaxLength(2000);

        // Не более одного активного прогона одновременно (Status=Running=0) — второй клик
        // "Перешифровать" или тик ночного добивателя, увидев конфликт вставки, присоединяется к
        // уже идущему прогону вместо старта нового (см. AdminKeysService).
        builder.HasIndex(r => r.Status)
            .IsUnique()
            .HasFilter("\"Status\" = 0");

        builder.HasIndex(r => r.StartedAt);
    }
}
