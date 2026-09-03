using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class GlobalMedicationKbConfiguration : IEntityTypeConfiguration<GlobalMedicationKb>
{
    public void Configure(EntityTypeBuilder<GlobalMedicationKb> builder)
    {
        // Отдельная схема kb: физическая изоляция обезличенного справочника от ПДн (задача 2.6).
        builder.ToTable("global_medications_kb", "kb");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.NormalizedName).HasMaxLength(300).IsRequired();
        builder.Property(k => k.DisplayName).HasMaxLength(300).IsRequired();
        builder.Property(k => k.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(k => k.Source).HasMaxLength(200).IsRequired();

        builder.Property(k => k.PayloadVersion).IsRequired();

        // Торговые названия (этап 4) — Postgres text[] с GIN-индексом, заводится raw SQL в
        // миграции AddMedicationEnrichment, как и search_vector: не единого кроссплатформенного
        // маппинга (Npgsql array vs SQLite-юнит-тесты), читается/пишется только через raw SQL
        // (KbLookupService/KbWriter) — исключаем из EF-модели, иначе SQLite-тесты не соберут модель.
        builder.Ignore(k => k.Aliases);
        builder.Ignore(k => k.LockedFields);

        builder.HasIndex(k => k.NormalizedName).IsUnique();
    }
}
