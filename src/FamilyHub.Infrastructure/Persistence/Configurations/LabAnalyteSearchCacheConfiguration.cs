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
        builder.Property(c => c.DisplayName).HasMaxLength(300);
        builder.Property(c => c.SpecimenKbId).IsRequired();
        builder.Property(c => c.SearchGroupKey).HasMaxLength(150).IsRequired();
        builder.Property(c => c.Provider).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Units).HasMaxLength(200);

        // Ключ — пара (показатель, группа поиска биоматериала): биоматериалы одной группы (кровь/венозная
        // кровь/плазма) делят одну строку кэша и один платный поиск (ADR-0018), SearchGroupKey у одиночного
        // биоматериала — "specimen:<id>", то есть прежнее поведение.
        builder.HasIndex(c => new { c.NormalizedName, c.SearchGroupKey }).IsUnique();
    }
}
