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

        // Тренд/поиск показателя ("мои показатели", GET /api/indicators/{analyteKey}/{specimen}) —
        // v2: Specimen в составе ключа, иначе лейкоциты крови и мочи слились бы на одном графике.
        builder.HasIndex(i => new { i.OwnerUserId, i.AnalyteKey, i.Specimen });

        // Upsert-мерж при повторном «Распознать» на записи с новым файлом (см.
        // MedicalDocumentExtractionProcessor) — находит существующую строку по этому же ключу.
        // Leftmost-префикс (MedicalRecordId) покрывает и «все показатели одного документа» —
        // отдельный одноколоночный индекс по MedicalRecordId был бы избыточен.
        builder.HasIndex(i => new { i.MedicalRecordId, i.AnalyteKey, i.Specimen }).IsUnique();

        // "Мои отклонения" — список показателей вне нормы по всем записям.
        builder.HasIndex(i => new { i.OwnerUserId, i.Flag });
    }
}
