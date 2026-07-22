using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicalRecordConfiguration : IEntityTypeConfiguration<MedicalRecord>
{
    public void Configure(EntityTypeBuilder<MedicalRecord> builder)
    {
        builder.ToTable("MedicalRecords", "medical");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.PersonName).HasMaxLength(200).IsRequired();

        // Главный фильтр видимости (раздел 6 брифа) бьёт по OwnerUserId — индекс обязателен.
        // Намеренно НЕ настраиваем FK на User: запись остаётся персональным ресурсом
        // без связи на семью.
        builder.HasIndex(r => r.OwnerUserId);
    }
}
