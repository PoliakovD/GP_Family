using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.ToTable("PushSubscriptions", "identity");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.EndpointHash).HasMaxLength(64).IsRequired();

        // Endpoint шифрован (AES-GCM, случайный nonce) — по нему саму колонку искать нельзя,
        // поэтому upsert/lookup идёт по EndpointHash (см. PushSubscription.EndpointHash).
        builder.HasIndex(s => s.EndpointHash).IsUnique();
        builder.HasIndex(s => s.UserId);
    }
}
