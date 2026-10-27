using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

/// <summary>Чат семьи (ADR-0019) — своя схема "chat". Все строки уходят каскадом и с семьёй, и с
/// пользователем: удаление аккаунта уносит его сообщения/реакции/отметки прочтения без явной чистки
/// (кроме вложений — FileAttachment полиморфен, без FK, см. AccountService/FamilyService).</summary>
public class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("ChatMessages", "chat");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Text).IsRequired();

        // Лента листается курсором (CreatedAt, Id) по убыванию внутри семьи.
        builder.HasIndex(m => new { m.FamilyId, m.CreatedAt, m.Id });
        builder.HasIndex(m => m.AuthorUserId);

        builder.HasOne(m => m.Family)
            .WithMany()
            .HasForeignKey(m => m.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Author)
            .WithMany()
            .HasForeignKey(m => m.AuthorUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.ReplyTo)
            .WithMany()
            .HasForeignKey(m => m.ReplyToMessageId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
