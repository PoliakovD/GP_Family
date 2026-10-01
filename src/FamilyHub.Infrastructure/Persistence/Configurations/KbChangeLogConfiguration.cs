using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class KbChangeLogConfiguration : IEntityTypeConfiguration<KbChangeLog>
{
    public void Configure(EntityTypeBuilder<KbChangeLog> builder)
    {
        // Схема kb — обезличенные снимки строк справочника (ADR-0018), тот же инвариант изоляции.
        builder.ToTable("change_log", "kb");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Actor).HasMaxLength(20).IsRequired();
        builder.Property(l => l.TargetLabel).HasMaxLength(400).IsRequired();
        builder.Property(l => l.Action).HasMaxLength(40).IsRequired();

        // История одной записи: свежие сверху.
        builder.HasIndex(l => new { l.Target, l.TargetId, l.At });
        builder.HasIndex(l => l.At);
    }
}
