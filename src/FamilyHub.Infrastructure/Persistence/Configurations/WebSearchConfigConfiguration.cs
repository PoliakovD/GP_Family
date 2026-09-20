using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class WebSearchConfigConfiguration : IEntityTypeConfiguration<WebSearchConfig>
{
    public void Configure(EntityTypeBuilder<WebSearchConfig> builder)
    {
        builder.ToTable("WebSearchConfigs");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Note).HasMaxLength(500);
    }
}
