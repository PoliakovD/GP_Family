using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class AdminSessionConfiguration : IEntityTypeConfiguration<AdminSession>
{
    public void Configure(EntityTypeBuilder<AdminSession> builder)
    {
        // public — инфраструктурное состояние панели (как CredentialRotations), не ПДн пользователей.
        builder.ToTable("AdminSessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.IpAddress).HasMaxLength(64);
        builder.Property(s => s.UserAgent).HasMaxLength(512);

        // «Выйти везде» отзывает все ещё не отозванные — частичный индекс по живым.
        builder.HasIndex(s => s.ExpiresAt).HasFilter("\"RevokedAt\" IS NULL");
    }
}
