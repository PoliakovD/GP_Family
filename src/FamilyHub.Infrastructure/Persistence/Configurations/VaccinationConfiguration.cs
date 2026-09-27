using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class VaccinationConfiguration : IEntityTypeConfiguration<Vaccination>
{
    public void Configure(EntityTypeBuilder<Vaccination> builder)
    {
        builder.ToTable("Vaccinations", "medical");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.SeriesCode).HasMaxLength(64);
        builder.Property(v => v.Kind).HasConversion<int>();
        builder.Property(v => v.DatePrecision).HasConversion<int?>();

        // «Мои прививки» и «прививки подопечных семьи» — главные выборки (как у MedicationCourse).
        builder.HasIndex(v => v.SubjectUserId);
        builder.HasIndex(v => v.FamilyDependentId);
        builder.HasIndex(v => v.CertificateId);

        // Одна доза календаря — одна запись на субъекта (повторная отметка правит существующую, не дублирует).
        builder.HasIndex(v => new { v.SubjectUserId, v.SeriesCode, v.DoseIndex })
            .IsUnique()
            .HasFilter("\"SubjectUserId\" IS NOT NULL AND \"SeriesCode\" IS NOT NULL");
        builder.HasIndex(v => new { v.FamilyDependentId, v.SeriesCode, v.DoseIndex })
            .IsUnique()
            .HasFilter("\"FamilyDependentId\" IS NOT NULL AND \"SeriesCode\" IS NOT NULL");

        // Ровно один субъект — без FK на User (чистится в AccountService), с FK CASCADE на подопечного.
        builder.ToTable(t => t.HasCheckConstraint("CK_Vaccinations_OneSubject",
            "(\"SubjectUserId\" IS NULL) <> (\"FamilyDependentId\" IS NULL)"));

        builder.HasOne<FamilyDependent>()
            .WithMany()
            .HasForeignKey(v => v.FamilyDependentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
