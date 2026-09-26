using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicationCourseConfiguration : IEntityTypeConfiguration<MedicationCourse>
{
    public void Configure(EntityTypeBuilder<MedicationCourse> builder)
    {
        builder.ToTable("MedicationCourses", "medical");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.ScheduleJson).HasColumnType("jsonb");
        builder.Property(c => c.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Food).HasConversion<int>();
        builder.Property(c => c.DoseUnit).HasConversion<int>();
        builder.Property(c => c.Status).HasConversion<int>();

        // «Мои курсы» и «курсы подопечных семьи» — главные выборки; фоновая задача берёт активные.
        // Намеренно без FK на User (SubjectUserId/CreatedByUserId) — как у HealthNote и MedicalRecord:
        // чистится явно в AccountService.
        builder.HasIndex(c => new { c.SubjectUserId, c.Status });
        builder.HasIndex(c => c.FamilyDependentId);
        builder.HasIndex(c => c.Status);
        builder.HasIndex(c => c.MedicationId);

        // Курс не переживает подопечного (тот же CASCADE, что у MedicalRecord.FamilyDependentId).
        builder.HasOne<FamilyDependent>()
            .WithMany()
            .HasForeignKey(c => c.FamilyDependentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Препарат из аптечки могут удалить — курс остаётся, просто без списания.
        builder.HasOne<Medication>()
            .WithMany()
            .HasForeignKey(c => c.MedicationId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
