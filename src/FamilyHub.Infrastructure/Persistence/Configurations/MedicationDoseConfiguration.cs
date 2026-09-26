using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicationDoseConfiguration : IEntityTypeConfiguration<MedicationDose>
{
    public void Configure(EntityTypeBuilder<MedicationDose> builder)
    {
        builder.ToTable("MedicationDoses", "medical");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Status).HasConversion<int>();
        builder.Property(d => d.Units).HasPrecision(8, 2);
        builder.Property(d => d.WriteOffUnits).HasPrecision(8, 2);

        // Один плановый приём — одна строка: страхует от гонки «джоба создаёт Pending» и
        // «пользователь отметил приём, которого ещё нет». Для «по необходимости» (ScheduledAt IS NULL)
        // уникальности нет — таких приёмов может быть сколько угодно.
        builder.HasIndex(d => new { d.CourseId, d.ScheduledAt })
            .IsUnique()
            .HasFilter("\"ScheduledAt\" IS NOT NULL");

        // Джобе нужны незакрытые приёмы (Pending/Snoozed), карточке — история по времени.
        builder.HasIndex(d => new { d.Status, d.ScheduledAt });

        builder.HasOne<MedicationCourse>()
            .WithMany()
            .HasForeignKey(d => d.CourseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
