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

        builder.HasIndex(k => k.NormalizedName).IsUnique();
    }
}
