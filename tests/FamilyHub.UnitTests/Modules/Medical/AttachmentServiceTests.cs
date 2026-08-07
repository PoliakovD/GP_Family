using System.Text;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class AttachmentServiceTests : SqliteTestBase
{
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly IFileCipher _fileCipher = new AesGcmFileCipher(
        Options.Create(new EncryptionOptions { MasterKey = DesignTimeDbContextFactory.DevMasterKey }));
    private readonly AttachmentService _sut;

    /// <summary>Байты, реально ушедшие в storage.SaveAsync (по ключу) — для проверок шифротекста.</summary>
    private readonly Dictionary<string, byte[]> _savedBlobs = [];

    public AttachmentServiceTests()
    {
        _storage.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                using var copy = new MemoryStream();
                callInfo.Arg<Stream>().CopyTo(copy);
                _savedBlobs[callInfo.ArgAt<string>(0)] = copy.ToArray();
                return Task.FromResult(callInfo.ArgAt<string>(0));
            });
        _storage.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<Stream>(new MemoryStream(_savedBlobs[callInfo.ArgAt<string>(0)])));

        var access = new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance);
        var auditWriter = new FamilyHub.Infrastructure.Audit.MedicalAuditWriter(Db);
        var medicalRecords = new MedicalRecordService(
            Db, access, new TestSupport.OutboxTestPipeline(Db).Writer, auditWriter,
            new RussianTextSearcher(), NullLogger<MedicalRecordService>.Instance);
        var downloadTokens = new DownloadTokenService(
            Options.Create(new AttachmentDownloadOptions { DownloadSigningKey = "test-download-signing-key" }));
        _sut = new AttachmentService(
            Db, _storage, _fileCipher, downloadTokens, medicalRecords, access, auditWriter, NullLogger<AttachmentService>.Instance);
    }

    private static MemoryStream Content() => new(Encoding.UTF8.GetBytes("scan-bytes"));

    [Fact]
    public async Task UploadForMedicalRecordAsync_Owner_SavesEncryptedBlobWithoutFileNameInKey()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();

        var (result, item) = await _sut.UploadForMedicalRecordAsync(
            record.Id, owner.Id, "scan.pdf", "application/pdf", 10, Content());

        result.Should().Be(AttachmentAccessResult.Success);
        item!.FileName.Should().Be("scan.pdf");

        var storageKey = _savedBlobs.Keys.Single();
        // Ключ полностью непрозрачен: ни recordId, ни какой-либо другой семантики родителя —
        // связь blob ↔ запись живёт только в FileAttachments.StorageKey (см. StorageKeyFactory).
        storageKey.Should().Be(StorageKeyFactory.Create(item!.Id));
        storageKey.Should().NotContain(record.Id.ToString());
        storageKey.Should().NotContain("scan.pdf", "имя файла — ПДн и не должно попадать в ключ хранилища");

        // В хранилище лежит шифротекст, а не исходные байты.
        _savedBlobs[storageKey].Should().NotBeEquivalentTo(Encoding.UTF8.GetBytes("scan-bytes"));
        Encoding.UTF8.GetString(_savedBlobs[storageKey]).Should().NotContain("scan-bytes");
        Db.FileAttachments.Single().IsEncrypted.Should().BeTrue();
    }

    [Fact]
    public async Task GetDownloadAsync_DecryptsBackToOriginalContent()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();
        var (_, item) = await _sut.UploadForMedicalRecordAsync(
            record.Id, owner.Id, "scan.pdf", "application/pdf", 10, Content());

        var download = await _sut.GetDownloadAsync(item!.Id);

        download.Should().NotBeNull();
        download!.Value.ContentType.Should().Be("application/pdf");
        download.Value.FileName.Should().Be("scan.pdf");
        using var reader = new StreamReader(download.Value.Content);
        (await reader.ReadToEndAsync()).Should().Be("scan-bytes");
    }

    [Fact]
    public async Task GetDownloadAsync_LegacyPlaintextAttachment_IsReturnedAsIs()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        var attachment = NewAttachment(FileOwnerType.MedicalRecord, record.Id);
        Db.FileAttachments.Add(attachment);
        await Db.SaveChangesAsync();
        _savedBlobs[attachment.StorageKey] = Encoding.UTF8.GetBytes("legacy-plain");

        var download = await _sut.GetDownloadAsync(attachment.Id);

        using var reader = new StreamReader(download!.Value.Content);
        (await reader.ReadToEndAsync()).Should().Be("legacy-plain");
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

        var ownerResult = await _sut.GetPresignedUrlAsync(attachment.Id, owner.Id);
        var outsiderResult = await _sut.GetPresignedUrlAsync(attachment.Id, outsider.Id);

        ownerResult.Result.Should().Be(AttachmentAccessResult.Success);
        ownerResult.Url.Should().StartWith($"/api/attachments/{attachment.Id}/file?expires=");
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
