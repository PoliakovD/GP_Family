using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class UserSpecimenConfiguration : IEntityTypeConfiguration<UserSpecimen>
{
    public void Configure(EntityTypeBuilder<UserSpecimen> builder)
    {
        builder.ToTable("UserSpecimens", "medical");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.NormalizedName).HasMaxLength(60).IsRequired();
        builder.Property(s => s.DisplayName).HasMaxLength(60).IsRequired();

        // Дедуп в пределах владельца — см. UserSpecimenService.CreateAsync.
        builder.HasIndex(s => new { s.OwnerUserId, s.NormalizedName }).IsUnique();
    }
}
