using System.Text;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class AttachmentServiceTests : SqliteTestBase
{
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly AttachmentService _sut;

    public AttachmentServiceTests()
    {
        var access = new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance);
        var medicalRecords = new MedicalRecordService(Db, access, NullLogger<MedicalRecordService>.Instance);
        _sut = new AttachmentService(Db, _storage, medicalRecords, access, NullLogger<AttachmentService>.Instance);
    }

    private static MemoryStream Content() => new(Encoding.UTF8.GetBytes("scan-bytes"));

    [Fact]
    public async Task UploadForMedicalRecordAsync_Owner_SavesAndReturnsDto()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();
        _storage.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("ignored"));

        var (result, item) = await _sut.UploadForMedicalRecordAsync(
            record.Id, owner.Id, "scan.pdf", "application/pdf", 10, Content());

        result.Should().Be(AttachmentAccessResult.Success);
        item!.FileName.Should().Be("scan.pdf");
        await _storage.Received(1).SaveAsync(
            Arg.Is<string>(k => k.Contains(record.Id.ToString())), Arg.Any<Stream>(), 10, "application/pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadForMedicalRecordAsync_NotOwner_ForbiddenAndDoesNotCallStorage()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();
        var someoneElse = Db.AddUser();

        var (result, item) = await _sut.UploadForMedicalRecordAsync(
            record.Id, someoneElse.Id, "scan.pdf", "application/pdf", 10, Content());

        result.Should().Be(AttachmentAccessResult.Forbidden);
        item.Should().BeNull();
        await _storage.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadForMedicalRecordAsync_UnknownRecord_NotFound()
    {
        var (result, item) = await _sut.UploadForMedicalRecordAsync(
            Guid.NewGuid(), Guid.NewGuid(), "scan.pdf", "application/pdf", 10, Content());

        result.Should().Be(AttachmentAccessResult.NotFound);
        item.Should().BeNull();
    }

    [Fact]
    public async Task GetPresignedUrlAsync_UnknownAttachment_NotFound()
    {
        var (result, url) = await _sut.GetPresignedUrlAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().Be(AttachmentAccessResult.NotFound);
        url.Should().BeNull();
    }

    [Fact]
    public async Task GetPresignedUrlAsync_MedicalRecordAttachment_AccessFollowsRecordVisibility()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        var attachment = NewAttachment(FileOwnerType.MedicalRecord, record.Id);
        Db.FileAttachments.Add(attachment);
        var outsider = Db.AddUser();
        await Db.SaveChangesAsync();
        _storage.GetPresignedUrlAsync(attachment.StorageKey, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("https://signed.example/file"));

        var ownerResult = await _sut.GetPresignedUrlAsync(attachment.Id, owner.Id);
        var outsiderResult = await _sut.GetPresignedUrlAsync(attachment.Id, outsider.Id);

        ownerResult.Result.Should().Be(AttachmentAccessResult.Success);
        ownerResult.Url.Should().Be("https://signed.example/file");
        outsiderResult.Result.Should().Be(AttachmentAccessResult.Forbidden);
    }

    [Fact]
    public async Task GetPresignedUrlAsync_MedicationAttachment_AccessFollowsFamilyRole()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        var medication = TestData.NewMedication(medkit.Id, family.Id, admin.Id);
        Db.Medications.Add(medication);
        var attachment = NewAttachment(FileOwnerType.Medication, medication.Id);
        Db.FileAttachments.Add(attachment);
        var outsider = Db.AddUser();
        await Db.SaveChangesAsync();
        _storage.GetPresignedUrlAsync(attachment.StorageKey, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("https://signed.example/file"));

        var memberResult = await _sut.GetPresignedUrlAsync(attachment.Id, admin.Id);
        var outsiderResult = await _sut.GetPresignedUrlAsync(attachment.Id, outsider.Id);

        memberResult.Result.Should().Be(AttachmentAccessResult.Success);
        outsiderResult.Result.Should().Be(AttachmentAccessResult.Forbidden);
    }

    private static FileAttachment NewAttachment(FileOwnerType ownerType, Guid ownerId) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = ownerType,
        OwnerId = ownerId,
        StorageKey = $"key/{Guid.NewGuid()}",
        FileName = "scan.pdf",
        ContentType = "application/pdf",
        SizeBytes = 10,
        IsEncrypted = false,
        UploadedAt = DateTime.UtcNow,
    };
}
