using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class PipelinePromptVersionConfiguration : IEntityTypeConfiguration<PipelinePromptVersion>
{
    public void Configure(EntityTypeBuilder<PipelinePromptVersion> builder)
    {
        builder.ToTable("PipelinePromptVersions");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.Body).IsRequired();
        builder.Property(v => v.Note).HasMaxLength(500);

        builder.HasOne(v => v.Prompt)
            .WithMany()
            .HasForeignKey(v => v.PromptId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(v => new { v.PromptId, v.Version }).IsUnique();

        // Не более одной активной версии на промпт одновременно — активация новой (см.
        // AdminPipelineEndpoints) обязана деактивировать предыдущую в той же транзакции.
        builder.HasIndex(v => v.PromptId)
            .IsUnique()
            .HasFilter("\"IsActive\" = true");
    }
}
