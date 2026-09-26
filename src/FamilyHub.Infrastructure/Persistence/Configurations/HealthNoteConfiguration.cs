using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class HealthNoteConfiguration : IEntityTypeConfiguration<HealthNote>
{
    public void Configure(EntityTypeBuilder<HealthNote> builder)
    {
        builder.ToTable("HealthNotes", "medical");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.Kind).HasConversion<int>();

        // Лента и выборки для отчёта: «мои записи за период», опционально по виду.
        // Намеренно без FK на User — как у MedicalRecord; чистится явно в AccountService.
        builder.HasIndex(n => new { n.OwnerUserId, n.OccurredAt });
        builder.HasIndex(n => new { n.OwnerUserId, n.Kind, n.OccurredAt });
    }
}
