using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class MedicationWatcherConfiguration : IEntityTypeConfiguration<MedicationWatcher>
{
    public void Configure(EntityTypeBuilder<MedicationWatcher> builder)
    {
        builder.ToTable("MedicationWatchers", "medical");

        builder.HasKey(w => w.Id);

        // Наблюдатель за конкретным субъектом — один раз; уникальность отдельно по виду субъекта.
        builder.HasIndex(w => new { w.SubjectUserId, w.WatcherUserId })
            .IsUnique()
            .HasFilter("\"SubjectUserId\" IS NOT NULL");
        builder.HasIndex(w => new { w.FamilyDependentId, w.WatcherUserId })
            .IsUnique()
            .HasFilter("\"FamilyDependentId\" IS NOT NULL");

        // «За кем я слежу» — выборка по наблюдателю.
        builder.HasIndex(w => w.WatcherUserId);

        // Ровно один субъект. SubjectUserId/WatcherUserId без FK на User — чистятся в AccountService.
        builder.ToTable(t => t.HasCheckConstraint("CK_MedicationWatchers_OneSubject",
            "(\"SubjectUserId\" IS NULL) <> (\"FamilyDependentId\" IS NULL)"));

        builder.HasOne<FamilyDependent>()
            .WithMany()
            .HasForeignKey(w => w.FamilyDependentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
