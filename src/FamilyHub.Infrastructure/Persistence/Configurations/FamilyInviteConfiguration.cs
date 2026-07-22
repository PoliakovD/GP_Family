using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class FamilyInviteConfiguration : IEntityTypeConfiguration<FamilyInvite>
{
    public void Configure(EntityTypeBuilder<FamilyInvite> builder)
    {
        builder.ToTable("FamilyInvites", "identity");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Code).HasMaxLength(64).IsRequired();
        builder.Property(i => i.AssignedRole).HasConversion<int>();

        builder.HasIndex(i => i.Code).IsUnique();

        // Семья может быть удалена не каскадно от инвайтов — но для каркаса
        // оставляем Restrict, чтобы явный код решал судьбу инвайтов.
        builder.HasOne(i => i.Family)
            .WithMany()
            .HasForeignKey(i => i.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
