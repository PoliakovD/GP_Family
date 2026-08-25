using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class FamilyDependentConfiguration : IEntityTypeConfiguration<FamilyDependent>
{
    public void Configure(EntityTypeBuilder<FamilyDependent> builder)
    {
        builder.ToTable("FamilyDependents", "identity");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(d => d.LastName).HasMaxLength(100);
        builder.Property(d => d.MiddleName).HasMaxLength(100);
        builder.Property(d => d.PetSpecies).HasMaxLength(100);

        builder.HasIndex(d => d.FamilyId);

        builder.HasOne(d => d.Family)
            .WithMany()
            .HasForeignKey(d => d.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
