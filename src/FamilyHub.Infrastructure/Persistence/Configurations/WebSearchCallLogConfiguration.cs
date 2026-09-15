using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class WebSearchCallLogConfiguration : IEntityTypeConfiguration<WebSearchCallLog>
{
    public void Configure(EntityTypeBuilder<WebSearchCallLog> builder)
    {
        // public — инфраструктурное состояние (как EncryptionRotationRuns), не ПДн/медданные.
        builder.ToTable("WebSearchCallLogs");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Provider).HasMaxLength(50).IsRequired();
        builder.Property(l => l.Topic).HasConversion<int>();
        builder.Property(l => l.NormalizedName).HasMaxLength(300).IsRequired();
        builder.Property(l => l.SpecimenDisplayName).HasMaxLength(200);
        builder.Property(l => l.QueryText).HasMaxLength(4000).IsRequired();
        builder.Property(l => l.Endpoint).HasMaxLength(200);
        builder.Property(l => l.Outcome).HasConversion<int>();
        builder.Property(l => l.Error).HasMaxLength(2000);
        builder.Property(l => l.JobKind).HasMaxLength(50);

        // Список /api/admin/search-calls — сортировка по времени, дефолт.
        builder.HasIndex(l => l.OccurredAt);
        // Разбивка по провайдеру за период (AdminSearchCallsEndpoints /stats).
        builder.HasIndex(l => new { l.Provider, l.OccurredAt });
        // Обратный поиск "по какому названию искали" из карточки KB/enrichment-задачи.
        builder.HasIndex(l => l.NormalizedName);
    }
}
