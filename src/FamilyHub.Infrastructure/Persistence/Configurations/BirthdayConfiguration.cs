using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class BirthdayConfiguration : IEntityTypeConfiguration<Birthday>
{
    public void Configure(EntityTypeBuilder<Birthday> builder)
    {
        builder.HasKey(b => b.Id);

        builder.Property(b => b.PersonName).HasMaxLength(200).IsRequired();

        builder.HasIndex(b => b.FamilyId);

        builder.HasOne(b => b.Family)
            .WithMany()
            .HasForeignKey(b => b.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
