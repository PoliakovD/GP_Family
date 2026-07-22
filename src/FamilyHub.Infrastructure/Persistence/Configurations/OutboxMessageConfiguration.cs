using FamilyHub.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();

        // Рабочая выборка диспетчера — только недоставленные, в порядке возникновения.
        // Частичный индекс: обработанные строки (подавляющее большинство) в него не попадают.
        builder.HasIndex(m => m.OccurredAt)
            .HasFilter("\"ProcessedAt\" IS NULL");
    }
}
