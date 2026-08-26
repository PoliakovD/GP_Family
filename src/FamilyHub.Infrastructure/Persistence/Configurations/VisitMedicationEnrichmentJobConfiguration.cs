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

        builder.HasIndex(j => j.NormalizedName)
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1)");

        builder.HasIndex(j => new { j.Status, j.CreatedAt });
        builder.HasIndex(j => j.MedicalRecordId);
    }
}
