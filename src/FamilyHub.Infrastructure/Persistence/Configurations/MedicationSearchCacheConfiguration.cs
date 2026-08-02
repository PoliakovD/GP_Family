using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicationSearchCacheConfiguration : IEntityTypeConfiguration<MedicationSearchCache>
{
    public void Configure(EntityTypeBuilder<MedicationSearchCache> builder)
    {
        // Схема kb — обезличенный кэш обращений к платному API, тот же инвариант изоляции, что и
        // у global_medications_kb (задача 2.6, KbIsolationGuardTests).
        builder.ToTable("medication_search_cache", "kb");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.NormalizedName).HasMaxLength(300).IsRequired();
        builder.Property(c => c.Provider).HasMaxLength(50).IsRequired();

        builder.HasIndex(c => c.NormalizedName).IsUnique();
    }
}
