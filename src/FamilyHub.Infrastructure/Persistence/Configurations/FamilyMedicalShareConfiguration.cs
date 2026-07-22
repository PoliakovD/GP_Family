using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class FamilyMedicalShareConfiguration : IEntityTypeConfiguration<FamilyMedicalShare>
{
    public void Configure(EntityTypeBuilder<FamilyMedicalShare> builder)
    {
        builder.ToTable("FamilyMedicalShares", "medical");

        builder.HasKey(s => s.Id);

        // УРОВЕНЬ 1: один владелец расшаривает свои анализы одной семье единожды.
        builder.HasIndex(s => new { s.OwnerUserId, s.FamilyId }).IsUnique();
    }
}
