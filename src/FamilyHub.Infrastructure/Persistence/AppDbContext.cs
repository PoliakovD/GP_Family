using System.Reflection;
using FamilyHub.Domain;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Outbox;
using FamilyHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FamilyHub.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, IFieldCipher fieldCipher) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Family> Families => Set<Family>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();
    public DbSet<FamilyInvite> FamilyInvites => Set<FamilyInvite>();
    public DbSet<FamilyInviteRedemption> FamilyInviteRedemptions => Set<FamilyInviteRedemption>();
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
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<GlobalMedicationKb> GlobalMedicationsKb => Set<GlobalMedicationKb>();
    public DbSet<PersonalCompatibilityResult> PersonalCompatibilityResults => Set<PersonalCompatibilityResult>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

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
