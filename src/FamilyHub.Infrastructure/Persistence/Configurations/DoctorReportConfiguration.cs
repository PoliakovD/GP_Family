using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class DoctorReportConfiguration : IEntityTypeConfiguration<DoctorReport>
{
    public void Configure(EntityTypeBuilder<DoctorReport> builder)
    {
        builder.ToTable("DoctorReports", "medical");

        builder.HasKey(r => r.Id);

        // Список «мои отчёты» — по владельцу, свежие сверху. Без FK на User (как MedicalRecord).
        builder.HasIndex(r => new { r.OwnerUserId, r.CreatedAt });

        // Публичная ссылка ищется по хешу токена. NULL-ы (нет ссылки) уникальность не нарушают.
        builder.Property(r => r.ShareTokenHash).HasMaxLength(64);
        builder.HasIndex(r => r.ShareTokenHash).IsUnique();
    }
}
