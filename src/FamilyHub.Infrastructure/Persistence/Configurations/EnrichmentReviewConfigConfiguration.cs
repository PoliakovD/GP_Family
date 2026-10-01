using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class EnrichmentReviewConfigConfiguration : IEntityTypeConfiguration<EnrichmentReviewConfig>
{
    public void Configure(EntityTypeBuilder<EnrichmentReviewConfig> builder)
    {
        builder.ToTable("EnrichmentReviewConfigs");

        builder.HasKey(c => c.Id);
    }
}
