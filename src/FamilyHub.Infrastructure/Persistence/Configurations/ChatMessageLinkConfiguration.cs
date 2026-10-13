using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class ChatMessageLinkConfiguration : IEntityTypeConfiguration<ChatMessageLink>
{
    public void Configure(EntityTypeBuilder<ChatMessageLink> builder)
    {
        builder.ToTable("ChatMessageLinks", "chat");

        builder.HasKey(l => l.Id);

        builder.HasIndex(l => l.MessageId);

        builder.HasOne(l => l.Message)
            .WithMany(m => m.Links)
            .HasForeignKey(l => l.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
