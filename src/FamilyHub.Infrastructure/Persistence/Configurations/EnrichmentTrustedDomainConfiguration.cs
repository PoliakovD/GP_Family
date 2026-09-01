using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class EnrichmentTrustedDomainConfiguration : IEntityTypeConfiguration<EnrichmentTrustedDomain>
{
    public void Configure(EntityTypeBuilder<EnrichmentTrustedDomain> builder)
    {
        builder.ToTable("EnrichmentTrustedDomains", "medical");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Topic).HasConversion<int>().IsRequired();
        builder.Property(d => d.Domain).HasMaxLength(200).IsRequired();

        builder.HasIndex(d => new { d.Topic, d.Domain }).IsUnique();
        builder.HasIndex(d => new { d.Topic, d.Rank });
    }
}
