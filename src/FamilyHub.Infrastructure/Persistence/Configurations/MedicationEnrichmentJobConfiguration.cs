using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicationEnrichmentJobConfiguration : IEntityTypeConfiguration<MedicationEnrichmentJob>
{
    public void Configure(EntityTypeBuilder<MedicationEnrichmentJob> builder)
    {
        // Схема medical (не kb!) — у задачи есть персональный контекст (кто попросил, в какой
        // семье), поэтому она не может лежать рядом с обезличенным справочником (KbIsolationGuardTests).
        builder.ToTable("MedicationEnrichmentJobs", "medical");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.NormalizedName).HasMaxLength(300).IsRequired();
        builder.Property(j => j.SourceDisplayName).HasMaxLength(300).IsRequired();
        builder.Property(j => j.Error).HasMaxLength(2000);
        builder.Property(j => j.Provider).HasMaxLength(50);

        // Дедуп внешних запросов: один и тот же препарат, сохранённый одновременно в разных
        // семьях, не должен породить два похода в интернет. Частичный индекс — только пока
        // задача жива (Pending=0/Running=1); завершённые (Completed/Failed/Skipped) не мешают
        // повторной попытке обогащения того же названия в будущем.
        builder.HasIndex(j => j.NormalizedName)
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1)");

        // Выборка очереди/статусов и карточки конкретного медикамента.
        builder.HasIndex(j => new { j.Status, j.CreatedAt });
        builder.HasIndex(j => j.MedicationId);
    }
}
