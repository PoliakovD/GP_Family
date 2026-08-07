using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicalRecordConfiguration : IEntityTypeConfiguration<MedicalRecord>
{
    public void Configure(EntityTypeBuilder<MedicalRecord> builder)
    {
        builder.ToTable("MedicalRecords", "medical");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.PersonName).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Kind).HasConversion<int>();
        builder.Property(r => r.ExtractionStatus).HasConversion<int>();

        // Главный фильтр видимости (раздел 6 брифа) бьёт по OwnerUserId — индекс обязателен.
        // Намеренно НЕ настраиваем FK на User: запись остаётся персональным ресурсом
        // без связи на семью.
        builder.HasIndex(r => r.OwnerUserId);

        // Списки/поиск теперь всегда фильтруют по виду записи (Kind не зашифрован — фильтр
        // применяется прямо в SQL, до расшифровки остальных полей).
        builder.HasIndex(r => new { r.OwnerUserId, r.Kind });

        // Обе новые ветки видимости (VisibleRecordsQuery) фильтруют по этим колонкам.
        builder.HasIndex(r => r.FamilyDependentId);
        builder.HasIndex(r => r.TargetUserId);

        // Единственный реальный FK на MedicalRecord — по требованию буквального DELETE CASCADE
        // при удалении подопечного (FamilyDependentService.DeleteAsync). Основной путь удаления
        // всё равно явный (собирает FileAttachment/MinIO-ключи ДО удаления строк) — этот FK лишь
        // защита на случай будущих обходных путей. TargetUserId остаётся FK-less, тот же
        // осознанный выбор, что и у OwnerUserId — запись не должна зависеть от связи на User.
        builder.HasOne<FamilyDependent>()
            .WithMany()
            .HasForeignKey(r => r.FamilyDependentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
