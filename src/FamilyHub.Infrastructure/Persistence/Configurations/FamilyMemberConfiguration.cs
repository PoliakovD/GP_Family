using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class FamilyMemberConfiguration : IEntityTypeConfiguration<FamilyMember>
{
    public void Configure(EntityTypeBuilder<FamilyMember> builder)
    {
        builder.ToTable("FamilyMembers", "identity");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Role).HasConversion<int>();
        builder.Property(m => m.Status).HasConversion<int>();

        // Членство в нескольких семьях уже заложено: один UserId может иметь
        // несколько строк, но не дублировать членство в одной и той же семье.
        builder.HasIndex(m => new { m.FamilyId, m.UserId }).IsUnique();
    }
}
