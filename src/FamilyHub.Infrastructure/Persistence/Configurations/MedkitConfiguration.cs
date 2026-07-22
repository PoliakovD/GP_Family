using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedkitConfiguration : IEntityTypeConfiguration<Medkit>
{
    public void Configure(EntityTypeBuilder<Medkit> builder)
    {
        builder.ToTable("Medkits", "medical");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Name).HasMaxLength(300).IsRequired();

        builder.HasIndex(m => m.FamilyId);

        builder.HasOne(m => m.Family)
            .WithMany()
            .HasForeignKey(m => m.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
