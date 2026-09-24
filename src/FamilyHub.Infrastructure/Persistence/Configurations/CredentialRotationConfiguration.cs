using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class CredentialRotationConfiguration : IEntityTypeConfiguration<CredentialRotation>
{
    public void Configure(EntityTypeBuilder<CredentialRotation> builder)
    {
        // public — инфраструктурное состояние (как EncryptionRotationRuns), не ПДн/медданные и не секреты.
        builder.ToTable("CredentialRotations");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Kind).HasConversion<int>();
        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.FromIdentity).HasMaxLength(255).IsRequired();
        builder.Property(r => r.ToIdentity).HasMaxLength(255).IsRequired();
        builder.Property(r => r.GeneratedBy).HasMaxLength(255).IsRequired();
        builder.Property(r => r.RevokedBy).HasMaxLength(255);

        // Не более одной ротации «ждёт деплоя» на вид учётки (Status=AwaitingDeploy=0) — два
        // параллельных клика «Сгенерировать» не создадут две конкурирующие пары секретов; вторая
        // вставка упрётся в индекс, а сервис до этого помечает предыдущую Superseded.
        builder.HasIndex(r => r.Kind)
            .IsUnique()
            .HasFilter("\"Status\" = 0");

        builder.HasIndex(r => r.GeneratedAt);
    }
}
