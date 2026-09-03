using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class PipelineStepConfigConfiguration : IEntityTypeConfiguration<PipelineStepConfig>
{
    public void Configure(EntityTypeBuilder<PipelineStepConfig> builder)
    {
        builder.ToTable("PipelineStepConfigs");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.PipelineKey).HasMaxLength(100).IsRequired();
        builder.Property(s => s.StepKey).HasMaxLength(100).IsRequired();

        builder.HasIndex(s => new { s.PipelineKey, s.StepKey }).IsUnique();
    }
}
