using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class VisitMedicationEnrichmentJobConfiguration : IEntityTypeConfiguration<VisitMedicationEnrichmentJob>
{
    public void Configure(EntityTypeBuilder<VisitMedicationEnrichmentJob> builder)
    {
        builder.ToTable("VisitMedicationEnrichmentJobs", "medical");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(j => j.SourceDisplayName).HasMaxLength(200).IsRequired();
        builder.Property(j => j.Error).HasMaxLength(2000);
        builder.Property(j => j.Provider).HasMaxLength(50);

        // Deferred=5 — вентиль платного поиска закрыт (ADR-0005 §9), задача жива, просто отложена.
        // 6/7 — AwaitingSearchApproval/AwaitingResultReview (ADR-0018): задача ждёт админа и жива —
        // без них в фильтре повторный запрос того же имени породил бы дубликат в очереди одобрения.
        builder.HasIndex(j => j.NormalizedName)
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1, 5, 6, 7)");

        builder.HasIndex(j => new { j.Status, j.CreatedAt });
        builder.HasIndex(j => j.MedicalRecordId);
    }
}
