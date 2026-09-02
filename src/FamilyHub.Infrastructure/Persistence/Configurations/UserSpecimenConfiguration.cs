using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class UserSpecimenConfiguration : IEntityTypeConfiguration<UserSpecimen>
{
    public void Configure(EntityTypeBuilder<UserSpecimen> builder)
    {
        builder.ToTable("UserSpecimens", "medical");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.SpecimenKbId).IsRequired();

        // SpecimenKbId — НЕ FK (GlobalSpecimenKb физически в отдельной схеме kb) — soft-ссылка.

        // Дедуп в пределах владельца — см. UserSpecimenService.RecordUsageAsync.
        builder.HasIndex(s => new { s.OwnerUserId, s.SpecimenKbId }).IsUnique();

        // Автоподсказка сортирует по недавности использования конкретным пользователем.
        builder.HasIndex(s => new { s.OwnerUserId, s.LastUsedAt });
    }
}
