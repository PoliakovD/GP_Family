using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class TelegramLinkCodeConfiguration : IEntityTypeConfiguration<TelegramLinkCode>
{
    public void Configure(EntityTypeBuilder<TelegramLinkCode> builder)
    {
        builder.ToTable("TelegramLinkCodes", "identity");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();

        // Проверка кода при /start в боте.
        builder.HasIndex(c => c.CodeHash);
        // Не более одного активного кода на пользователя (см. TelegramLinkService.StartAsync —
        // сервис сам инвалидирует прежние коды перед выдачей нового).
        builder.HasIndex(c => c.UserId);
    }
}
