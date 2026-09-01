using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class GlobalSpecimenKbConfiguration : IEntityTypeConfiguration<GlobalSpecimenKb>
{
    public void Configure(EntityTypeBuilder<GlobalSpecimenKb> builder)
    {
        // Схема kb — обезличенный общий справочник, тот же инвариант изоляции, что и у остальных
        // kb-таблиц (задача 2.6, KbIsolationGuardTests).
        builder.ToTable("global_specimens_kb", "kb");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(s => s.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Source).HasMaxLength(50).IsRequired();

        builder.HasIndex(s => s.NormalizedName).IsUnique();
    }
}
