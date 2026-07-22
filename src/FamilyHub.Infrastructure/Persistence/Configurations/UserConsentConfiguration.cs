using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class UserConsentConfiguration : IEntityTypeConfiguration<UserConsent>
{
    public void Configure(EntityTypeBuilder<UserConsent> builder)
    {
        builder.ToTable("UserConsents", "identity");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Version).HasMaxLength(50).IsRequired();

        // Идемпотентность повторного принятия одной версии.
        builder.HasIndex(c => new { c.UserId, c.Kind, c.Version }).IsUnique();
    }
}
