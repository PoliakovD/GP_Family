using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicationConfiguration : IEntityTypeConfiguration<Medication>
{
    public void Configure(EntityTypeBuilder<Medication> builder)
    {
        builder.ToTable("Medications", "medical");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Name).HasMaxLength(300).IsRequired();
        builder.Property(m => m.DataJson).HasColumnType("jsonb");

        builder.HasIndex(m => m.FamilyId);
        builder.HasIndex(m => m.MedkitId);
        builder.HasIndex(m => m.ExpiryDate); // для джобы оповещений о сроке годности

        builder.HasOne(m => m.Family)
            .WithMany()
            .HasForeignKey(m => m.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Medkit)
            .WithMany(k => k.Medications)
            .HasForeignKey(m => m.MedkitId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
