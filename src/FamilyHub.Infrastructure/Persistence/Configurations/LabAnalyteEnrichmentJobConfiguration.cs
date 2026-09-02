using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class LabAnalyteEnrichmentJobConfiguration : IEntityTypeConfiguration<LabAnalyteEnrichmentJob>
{
    public void Configure(EntityTypeBuilder<LabAnalyteEnrichmentJob> builder)
    {
        builder.ToTable("LabAnalyteEnrichmentJobs", "medical");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(j => j.SpecimenKbId).IsRequired();
        builder.Property(j => j.SourceDisplayName).HasMaxLength(200).IsRequired();
        builder.Property(j => j.Error).HasMaxLength(2000);
        builder.Property(j => j.Provider).HasMaxLength(50);

        // Дедуп-индекс — пара (показатель, источник), не одно имя (пересборка enrich-пайплайна):
        // "белок" в крови и в моче не должны конкурировать за одну Pending/Running-задачу.
        builder.HasIndex(j => new { j.NormalizedName, j.SpecimenKbId })
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1)");

        builder.HasIndex(j => new { j.Status, j.CreatedAt });
        builder.HasIndex(j => j.LabIndicatorId);
    }
}
