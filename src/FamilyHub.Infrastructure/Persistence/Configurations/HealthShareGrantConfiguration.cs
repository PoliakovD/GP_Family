using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class HealthShareGrantConfiguration : IEntityTypeConfiguration<HealthShareGrant>
{
    public void Configure(EntityTypeBuilder<HealthShareGrant> builder)
    {
        builder.ToTable("HealthShareGrants", "medical");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Categories).HasConversion<int>();

        // Максимум одна строка на пару — SetAsync читает/пишет её целиком, не по одной категории.
        builder.HasIndex(g => new { g.OwnerUserId, g.ViewerUserId }).IsUnique();

        // «Кому я дал доступ» / «кто дал доступ мне» — обе стороны нужны почти в равной мере
        // (HealthShareEndpoints.mine и .shared-with-me), поэтому индекс и на ViewerUserId тоже.
        builder.HasIndex(g => g.ViewerUserId);

        // Без FK на User — как и MedicationWatcher, чистится явно в AccountService при удалении аккаунта.
        builder.ToTable(t => t.HasCheckConstraint("CK_HealthShareGrants_NotSelf", "\"OwnerUserId\" <> \"ViewerUserId\""));
    }
}
