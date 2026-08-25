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

        // Наследует видимость MedicalRecord — удаление записи должно удалять её показатели.
        builder.HasOne<MedicalRecord>()
            .WithMany()
            .HasForeignKey(i => i.MedicalRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        // Тренд/поиск показателя ("мои показатели", GET /api/indicators/{analyteKey}).
        builder.HasIndex(i => new { i.OwnerUserId, i.AnalyteKey });

        // Все показатели одного документа (пересчёт при повторном распознавании).
        builder.HasIndex(i => i.MedicalRecordId);

        // "Мои отклонения" — список показателей вне нормы по всем записям.
        builder.HasIndex(i => new { i.OwnerUserId, i.Flag });
    }
}
