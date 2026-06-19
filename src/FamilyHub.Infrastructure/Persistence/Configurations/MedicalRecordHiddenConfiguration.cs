using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicalRecordHiddenConfiguration : IEntityTypeConfiguration<MedicalRecordHidden>
{
    public void Configure(EntityTypeBuilder<MedicalRecordHidden> builder)
    {
        builder.HasKey(h => h.Id);

        // УРОВЕНЬ 2: одна запись скрыта от одной семьи не более одного раза.
        builder.HasIndex(h => new { h.MedicalRecordId, h.FamilyId }).IsUnique();

        builder.HasOne(h => h.MedicalRecord)
            .WithMany()
            .HasForeignKey(h => h.MedicalRecordId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
