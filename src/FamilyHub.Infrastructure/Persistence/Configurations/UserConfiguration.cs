using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", "identity");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(u => u.Username).HasMaxLength(32);
        builder.Property(u => u.TgUsername).HasMaxLength(32);
        builder.Property(u => u.Email).HasMaxLength(320);
        builder.Property(u => u.PinHash).HasMaxLength(200);

        // Оба идентификатора входа уникальны среди заполненных (nullable с этапа 2:
        // Telegram-only и PWA-only пользователи сосуществуют).
        builder.HasIndex(u => u.TelegramId).IsUnique().HasFilter("\"TelegramId\" IS NOT NULL");
        builder.HasIndex(u => u.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");

        // Видимый username уникален среди заполненных; TgUsername (зеркало TG-хэндла) —
        // намеренно без уникальности, это просто отображаемый атрибут профиля.
        builder.HasIndex(u => u.Username).IsUnique().HasFilter("\"Username\" IS NOT NULL");

        builder.HasMany(u => u.Memberships)
            .WithOne(m => m.User)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
