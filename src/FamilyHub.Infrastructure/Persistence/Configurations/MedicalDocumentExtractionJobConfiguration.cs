using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicalDocumentExtractionJobConfiguration : IEntityTypeConfiguration<MedicalDocumentExtractionJob>
{
    public void Configure(EntityTypeBuilder<MedicalDocumentExtractionJob> builder)
    {
        builder.ToTable("MedicalDocumentExtractionJobs", "medical");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Status).HasConversion<int>();
        builder.Property(j => j.Stage).HasConversion<int>();
        builder.Property(j => j.Error).HasMaxLength(2000);

        // Дедуп повторного клика «Распознать» на одном вложении, пока задача жива — тот же
        // приём, что частичный индекс по NormalizedName у MedicationEnrichmentJob.
        builder.HasIndex(j => j.AttachmentId)
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1)");

        builder.HasIndex(j => j.MedicalRecordId);
        builder.HasIndex(j => new { j.Status, j.CreatedAt });
    }
}
