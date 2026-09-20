using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class SearchWarmupRunConfiguration : IEntityTypeConfiguration<SearchWarmupRun>
{
    public void Configure(EntityTypeBuilder<SearchWarmupRun> builder)
    {
        // public — инфраструктурное состояние (как KbRebuildRun/EncryptionRotationRun), не медданные.
        builder.ToTable("SearchWarmupRuns");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.LastError).HasMaxLength(2000);
        builder.Property(r => r.NamesJson).IsRequired();

        // Не более одной строки в каждом "живом" статусе одновременно — тот же приём, что
        // KbRebuildRun/EncryptionRotationRun (unique-индекс на САМО значение статуса корректно
        // ограничивает ровно одним рядом только для ОДНОГО значения фильтра; для "хотя бы один
        // Running ИЛИ Paused" единственная гарантия — прикладная проверка в
        // AdminSearchWarmupService.StartAsync, здесь только защита от двух Running и от двух
        // Paused по отдельности). Paused появляется только с вентилем (этап 2 плана) — второй
        // такой индекс на Status=1 добавится вместе с ним.
        builder.HasIndex(r => r.Status)
            .IsUnique()
            .HasFilter("\"Status\" = 0");

        builder.HasIndex(r => r.StartedAt);
    }
}
