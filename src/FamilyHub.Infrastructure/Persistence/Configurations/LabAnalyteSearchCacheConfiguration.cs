using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class LabAnalyteSearchCacheConfiguration : IEntityTypeConfiguration<LabAnalyteSearchCache>
{
    public void Configure(EntityTypeBuilder<LabAnalyteSearchCache> builder)
    {
        // Схема kb — обезличенный кэш обращений к платному API, тот же инвариант изоляции, что и
        // у global_lab_analytes_kb/medication_search_cache (задача 2.6, KbIsolationGuardTests).
        builder.ToTable("lab_analyte_search_cache", "kb");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.NormalizedName).HasMaxLength(300).IsRequired();
        builder.Property(c => c.SpecimenKbId).IsRequired();
        builder.Property(c => c.Provider).HasMaxLength(50).IsRequired();

        // Ключ — пара (показатель, источник), не одно имя (пересборка enrich-пайплайна).
        builder.HasIndex(c => new { c.NormalizedName, c.SpecimenKbId }).IsUnique();
    }
}
