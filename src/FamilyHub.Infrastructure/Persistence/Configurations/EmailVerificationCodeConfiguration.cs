using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class EmailVerificationCodeConfiguration : IEntityTypeConfiguration<EmailVerificationCode>
{
    public void Configure(EntityTypeBuilder<EmailVerificationCode> builder)
    {
        builder.ToTable("EmailVerificationCodes", "identity");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Email).HasMaxLength(320).IsRequired();
        builder.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();

        // Выборки: активные коды адресата (троттлинг выдачи) и проверка при подтверждении.
        builder.HasIndex(c => new { c.Email, c.Purpose });
    }
}
