using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class LabIndicatorConfiguration : IEntityTypeConfiguration<LabIndicator>
{
    public void Configure(EntityTypeBuilder<LabIndicator> builder)
    {
        builder.ToTable("LabIndicators", "medical");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.AnalyteKey).HasMaxLength(200).IsRequired();
        builder.Property(i => i.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.Flag).HasConversion<int>();
        builder.Property(i => i.RefSource).HasConversion<int>();
        builder.Property(i => i.Specimen).HasConversion<int>();

        // Наследует видимость MedicalRecord — удаление записи должно удалять её показатели.
        builder.HasOne<MedicalRecord>()
            .WithMany()
            .HasForeignKey(i => i.MedicalRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        // Кастомный биоматериал (UX-редизайн) — Restrict, а не Cascade: удаление записи
        // справочника UserSpecimens не должно молча уносить с собой чужие показатели, ссылка
        // снимается только через явный UserSpecimenService.
        builder.HasOne<UserSpecimen>()
            .WithMany()
            .HasForeignKey(i => i.SpecimenCustomId)
            .OnDelete(DeleteBehavior.Restrict);

        // Тренд/поиск показателя ("мои показатели", GET /api/indicators/{analyteKey}) —
        // v2: Specimen+SpecimenCustomId в составе ключа, иначе лейкоциты крови и мочи (или два
        // разных кастомных биоматериала, оба Specimen=Other) слились бы на одном графике.
        builder.HasIndex(i => new { i.OwnerUserId, i.AnalyteKey, i.Specimen, i.SpecimenCustomId });

        // Upsert-мерж при повторном «Распознать» на записи с новым файлом (см.
        // MedicalDocumentExtractionProcessor) — находит существующую строку по этому же ключу.
        // Leftmost-префикс (MedicalRecordId) покрывает и «все показатели одного документа» —
        // отдельный одноколоночный индекс по MedicalRecordId был бы избыточен.
        // AreNullsDistinct(false) — Postgres по умолчанию считает NULL != NULL, что сломало бы
        // дедуп для всех показателей без кастомного биоматериала (SpecimenCustomId == null почти
        // всегда): без этого флага каждый upsert с null создавал бы новую строку вместо мержа.
        builder.HasIndex(i => new { i.MedicalRecordId, i.AnalyteKey, i.Specimen, i.SpecimenCustomId })
            .IsUnique()
            .AreNullsDistinct(false);

        // "Мои отклонения" — список показателей вне нормы по всем записям.
        builder.HasIndex(i => new { i.OwnerUserId, i.Flag });
    }
}
