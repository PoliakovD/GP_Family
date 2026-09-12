using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class LmStudioReasoningConfigConfiguration : IEntityTypeConfiguration<LmStudioReasoningConfig>
{
    public void Configure(EntityTypeBuilder<LmStudioReasoningConfig> builder)
    {
        builder.ToTable("LmStudioReasoningConfigs");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Reasoning).HasConversion<int>();
    }
}
