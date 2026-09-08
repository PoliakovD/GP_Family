using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class AttachmentPreviewConfiguration : IEntityTypeConfiguration<AttachmentPreview>
{
    public void Configure(EntityTypeBuilder<AttachmentPreview> builder)
    {
        builder.ToTable("AttachmentPreviews", "medical");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Kind).HasConversion<int>();
        builder.Property(p => p.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(p => p.ContentType).HasMaxLength(150).IsRequired();
        builder.Property(p => p.KeyId).HasMaxLength(255);

        // Ровно один артефакт каждого вида на вложение (дедуп повторной генерации).
        builder.HasIndex(p => new { p.AttachmentId, p.Kind }).IsUnique();

        // Явный FK с каскадным удалением — превью не переживает своё вложение (в отличие от
        // FileAttachment, у которого своей внешней ссылки на родителя нет — тут она есть, потому
        // что AttachmentPreview принадлежит конкретно FileAttachment, а не абстрактному Owner).
        builder.HasOne<FileAttachment>()
            .WithMany()
            .HasForeignKey(p => p.AttachmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
