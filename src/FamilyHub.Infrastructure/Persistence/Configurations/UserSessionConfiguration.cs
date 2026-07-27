using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("UserSessions", "identity");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.RefreshTokenHash).HasMaxLength(64).IsRequired();

        // Lookup при /api/auth/refresh — по хешу предъявленного токена.
        builder.HasIndex(t => t.RefreshTokenHash).IsUnique();
        // RevokeAllForUserAsync (logout-all / account erasure) — все активные сессии пользователя.
        builder.HasIndex(t => t.UserId);
    }
}
