using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class DoseActionTokenConfiguration : IEntityTypeConfiguration<DoseActionToken>
{
    public void Configure(EntityTypeBuilder<DoseActionToken> builder)
    {
        builder.ToTable("DoseActionTokens", "medical");

        builder.HasKey(t => t.Id);

        // SHA-256 в hex — 64 символа; по нему анонимный эндпоинт ищет токен.
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(t => t.UsedAction).HasConversion<int?>();

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.DoseId);
        builder.HasIndex(t => t.ExpiresAt); // чистка просроченных
        builder.HasIndex(t => t.RecipientUserId);

        builder.HasOne<MedicationDose>()
            .WithMany()
            .HasForeignKey(t => t.DoseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
