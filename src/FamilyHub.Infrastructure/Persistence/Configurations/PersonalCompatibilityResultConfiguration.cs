using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class PersonalCompatibilityResultConfiguration : IEntityTypeConfiguration<PersonalCompatibilityResult>
{
    public void Configure(EntityTypeBuilder<PersonalCompatibilityResult> builder)
    {
        builder.ToTable("PersonalCompatibilityResults", "medical");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.InputHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.ResultJson).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.ModelVersion).HasMaxLength(100).IsRequired();

        // Кэш повторного анализа того же набора препаратов у того же пользователя.
        builder.HasIndex(r => new { r.UserId, r.InputHash }).IsUnique();
    }
}
