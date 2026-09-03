using System.Reflection;
using FamilyHub.Domain;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Security;
using MassTransit;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FamilyHub.Infrastructure.Persistence;

/// <summary>
/// IDataProtectionKeyContext (добавлено 2026-08-20) — ключи Data Protection (CSRF-токены,
/// IAntiforgery) хранятся в той же Postgres, что и остальное состояние, а не в эфемерной ФС
/// контейнера, см. DataProtectionKeys ниже и AddDataProtection в Program.cs.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, IFieldCipher fieldCipher)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Family> Families => Set<Family>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();
    public DbSet<FamilyInvite> FamilyInvites => Set<FamilyInvite>();
    public DbSet<FamilyInviteRedemption> FamilyInviteRedemptions => Set<FamilyInviteRedemption>();
    public DbSet<FamilyDependent> FamilyDependents => Set<FamilyDependent>();
    public DbSet<Medkit> Medkits => Set<Medkit>();
    public DbSet<Medication> Medications => Set<Medication>();
    public DbSet<MedicalRecord> MedicalRecords => Set<MedicalRecord>();
    public DbSet<FamilyMedicalShare> FamilyMedicalShares => Set<FamilyMedicalShare>();
    public DbSet<MedicalRecordHidden> MedicalRecordHiddens => Set<MedicalRecordHidden>();
    public DbSet<FileAttachment> FileAttachments => Set<FileAttachment>();
    public DbSet<Birthday> Birthdays => Set<Birthday>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<EmailVerificationCode> EmailVerificationCodes => Set<EmailVerificationCode>();
    public DbSet<TelegramLinkCode> TelegramLinkCodes => Set<TelegramLinkCode>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<UserNotificationPreference> UserNotificationPreferences => Set<UserNotificationPreference>();
    public DbSet<GlobalMedicationKb> GlobalMedicationsKb => Set<GlobalMedicationKb>();
    public DbSet<PersonalCompatibilityResult> PersonalCompatibilityResults => Set<PersonalCompatibilityResult>();
    public DbSet<MedicationEnrichmentJob> MedicationEnrichmentJobs => Set<MedicationEnrichmentJob>();
    public DbSet<MedicationSearchCache> MedicationSearchCaches => Set<MedicationSearchCache>();
    public DbSet<EnrichmentTrustedDomain> EnrichmentTrustedDomains => Set<EnrichmentTrustedDomain>();

    /// <summary>Ветка medicalrecords (задачи 5.2/5.3) — конвейер извлечения показателей анализов
    /// и заключений врача.</summary>
    public DbSet<LabIndicator> LabIndicators => Set<LabIndicator>();
    public DbSet<MedicalDocumentExtractionJob> MedicalDocumentExtractionJobs => Set<MedicalDocumentExtractionJob>();
    public DbSet<GlobalLabAnalyteKb> GlobalLabAnalytesKb => Set<GlobalLabAnalyteKb>();
    public DbSet<LabAnalyteEnrichmentJob> LabAnalyteEnrichmentJobs => Set<LabAnalyteEnrichmentJob>();
    public DbSet<LabAnalyteSearchCache> LabAnalyteSearchCaches => Set<LabAnalyteSearchCache>();
    public DbSet<GlobalSpecimenKb> GlobalSpecimensKb => Set<GlobalSpecimenKb>();

    /// <summary>Прогоны пересборки справочника показателей (пересборка enrich-пайплайна, §4.2) —
    /// см. LabAnalyteKbRebuildJob.</summary>
    public DbSet<KbRebuildRun> KbRebuildRuns => Set<KbRebuildRun>();

    /// <summary>Управление enrich-пайплайном из админки (§2) — слоты промптов, их версии
    /// и вкл/выкл шагов. См. PromptProvider, PipelineConfigService.</summary>
    public DbSet<PipelinePrompt> PipelinePrompts => Set<PipelinePrompt>();
    public DbSet<PipelinePromptVersion> PipelinePromptVersions => Set<PipelinePromptVersion>();
    public DbSet<PipelineStepConfig> PipelineStepConfigs => Set<PipelineStepConfig>();

    /// <summary>Пользовательский справочник биоматериалов (UX-редизайн) — см. UserSpecimenService.</summary>
    public DbSet<UserSpecimen> UserSpecimens => Set<UserSpecimen>();

    /// <summary>Обогащение kb.global_medications_kb для препаратов из заключений врача
    /// (UX-редизайн) — см. VisitMedicationEnrichmentProcessor.</summary>
    public DbSet<VisitMedicationEnrichmentJob> VisitMedicationEnrichmentJobs => Set<VisitMedicationEnrichmentJob>();

    /// <summary>Прогоны перешифровки при ротации ключа (ADR-0009) — см. EncryptionRotationJob.</summary>
    public DbSet<EncryptionRotationRun> EncryptionRotationRuns => Set<EncryptionRotationRun>();

    /// <summary>Требуется интерфейсом IDataProtectionKeyContext — имя DbSet фиксировано пакетом,
    /// не наша конвенция именования.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Таблицы шины MassTransit (ADR-0006): InboxState/OutboxState/OutboxMessage — замена
        // собственной таблицы OutboxMessages. InboxState заведена сразу (миграция не потребуется
        // повторно), хотя фильтр дедупликации на приёме пока не включён — см. Messaging/.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();

        // At-rest шифрование [Encrypted]-полей (этап 2, 152-ФЗ). ВНИМАНИЕ: EF кэширует модель
        // на процесс — конвертер захватывает cipher первого экземпляра контекста, поэтому
        // IFieldCipher обязан быть синглтоном со стабильным ключом на всё время работы процесса.
        var encryptedConverter = new ValueConverter<string, string>(
            v => fieldCipher.Protect(v),
            v => fieldCipher.Unprotect(v));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() is null) continue;

                property.SetValueConverter(encryptedConverter);
                // Шифротекст длиннее исходника — ограничения varchar(N) снимаются (колонка → text).
                property.SetMaxLength(null);
            }
        }
    }
}
