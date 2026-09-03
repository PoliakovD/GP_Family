using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class PipelinePromptConfiguration : IEntityTypeConfiguration<PipelinePrompt>
{
    public void Configure(EntityTypeBuilder<PipelinePrompt> builder)
    {
        builder.ToTable("PipelinePrompts");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Key).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(500).IsRequired();

        builder.HasIndex(p => p.Key).IsUnique();
    }
}
