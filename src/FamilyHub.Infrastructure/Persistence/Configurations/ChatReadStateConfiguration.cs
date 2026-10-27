using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class ChatReadStateConfiguration : IEntityTypeConfiguration<ChatReadState>
{
    public void Configure(EntityTypeBuilder<ChatReadState> builder)
    {
        builder.ToTable("ChatReadStates", "chat");

        builder.HasKey(s => new { s.FamilyId, s.UserId });

        builder.HasIndex(s => s.UserId);

        builder.HasOne<Family>()
            .WithMany()
            .HasForeignKey(s => s.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
