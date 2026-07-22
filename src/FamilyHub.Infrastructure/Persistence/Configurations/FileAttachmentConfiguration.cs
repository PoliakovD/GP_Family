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

        // Доступ наследуется от родителя — нужен быстрый поиск вложений родительской записи.
        builder.HasIndex(f => new { f.OwnerType, f.OwnerId });
    }
}
