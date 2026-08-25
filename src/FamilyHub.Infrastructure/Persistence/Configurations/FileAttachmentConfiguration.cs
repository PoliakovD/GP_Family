using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class FileAttachmentConfiguration : IEntityTypeConfiguration<FileAttachment>
{
    public void Configure(EntityTypeBuilder<FileAttachment> builder)
    {
        builder.ToTable("FileAttachments", "medical");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.OwnerType).HasConversion<int>();
        builder.Property(f => f.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(f => f.FileName).HasMaxLength(300).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(150).IsRequired();
        builder.Property(f => f.KeyId).HasMaxLength(255);

        // Доступ наследуется от родителя — нужен быстрый поиск вложений родительской записи.
        builder.HasIndex(f => new { f.OwnerType, f.OwnerId });

        // Отчёт о ходе ротации (EncryptionRotationJob/админка, ADR-0009): "сколько блобов ещё не
        // на активном ключе" — частичный индекс, только зашифрованные строки (KeyId IS NULL у
        // legacy-вложений участвовать не должен и не будет отобран условием IsEncrypted).
        builder.HasIndex(f => new { f.IsEncrypted, f.KeyId });
    }
}
