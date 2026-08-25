using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class GlobalLabAnalyteKbConfiguration : IEntityTypeConfiguration<GlobalLabAnalyteKb>
{
    public void Configure(EntityTypeBuilder<GlobalLabAnalyteKb> builder)
    {
        // Отдельная схема kb — та же физическая изоляция от ПДн, что у global_medications_kb.
        builder.ToTable("global_lab_analytes_kb", "kb");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(k => k.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(k => k.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(k => k.Source).HasMaxLength(200).IsRequired();
        builder.Property(k => k.PayloadVersion).IsRequired();

        // Aliases — Postgres text[], заводится raw SQL в миграции (как у GlobalMedicationKb.Aliases) —
        // не единого кроссплатформенного маппинга для SQLite-юнит-тестов.
        builder.Ignore(k => k.Aliases);

        builder.HasIndex(k => k.NormalizedName).IsUnique();
    }
}
