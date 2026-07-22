using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications", "identity");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.Title).HasMaxLength(300).IsRequired();
        builder.Property(n => n.Body).HasMaxLength(2000).IsRequired();
        builder.Property(n => n.DedupKey).HasMaxLength(200).IsRequired();

        // Идемпотентность повторных прогонов джобы: один и тот же повод не дублируется.
        builder.HasIndex(n => n.DedupKey).IsUnique();

        // Основная выборка — "мои непрочитанные оповещения".
        builder.HasIndex(n => new { n.UserId, n.IsRead });

        builder.HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(n => n.Family)
            .WithMany()
            .HasForeignKey(n => n.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
