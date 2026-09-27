using FamilyHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyHub.Infrastructure.Persistence.Configurations;

public class VaccinationCertificateConfiguration : IEntityTypeConfiguration<VaccinationCertificate>
{
    public void Configure(EntityTypeBuilder<VaccinationCertificate> builder)
    {
        builder.ToTable("VaccinationCertificates", "medical");

        builder.HasKey(c => c.Id);

        builder.HasIndex(c => c.SubjectUserId);
        builder.HasIndex(c => c.FamilyDependentId);

        builder.ToTable(t => t.HasCheckConstraint("CK_VaccinationCertificates_OneSubject",
            "(\"SubjectUserId\" IS NULL) <> (\"FamilyDependentId\" IS NULL)"));

        builder.HasOne<FamilyDependent>()
            .WithMany()
            .HasForeignKey(c => c.FamilyDependentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
