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
