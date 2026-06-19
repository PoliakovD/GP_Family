using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class FamilyInviteRedemptionConfiguration : IEntityTypeConfiguration<FamilyInviteRedemption>
{
    public void Configure(EntityTypeBuilder<FamilyInviteRedemption> builder)
    {
        builder.HasKey(r => r.Id);

        builder.HasIndex(r => new { r.FamilyInviteId, r.UserId }).IsUnique();

        builder.HasOne(r => r.FamilyInvite)
            .WithMany()
            .HasForeignKey(r => r.FamilyInviteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
