using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
