using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicationConfiguration : IEntityTypeConfiguration<Medication>
{
    public void Configure(EntityTypeBuilder<Medication> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Name).HasMaxLength(300).IsRequired();

        builder.HasIndex(m => m.FamilyId);
        builder.HasIndex(m => m.ExpiryDate); // для джобы оповещений о сроке годности

        builder.HasOne(m => m.Family)
            .WithMany()
            .HasForeignKey(m => m.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
