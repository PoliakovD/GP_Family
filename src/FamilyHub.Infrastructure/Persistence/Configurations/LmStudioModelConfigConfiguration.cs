using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class LmStudioModelConfigConfiguration : IEntityTypeConfiguration<LmStudioModelConfig>
{
    public void Configure(EntityTypeBuilder<LmStudioModelConfig> builder)
    {
        builder.ToTable("LmStudioModelConfigs");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.ModelId).HasMaxLength(200).IsRequired();
    }
}
