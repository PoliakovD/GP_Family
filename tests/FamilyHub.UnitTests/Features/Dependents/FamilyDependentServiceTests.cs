using FamilyHub.Api.Features.Dependents;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Features.Dependents;

public class FamilyDependentServiceTests : SqliteTestBase
{
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly FamilyDependentService _sut;

    public FamilyDependentServiceTests()
    {
        var access = new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance);
        _sut = new FamilyDependentService(Db, access, _storage, NullLogger<FamilyDependentService>.Instance);
    }

    [Fact]
    public async Task CreateAsync_Member_Succeeds()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var (result, item) = await _sut.CreateAsync(
            family.Id, admin.Id, new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот"));

        result.Should().Be(FamilyDependentAccessResult.Success);
        item!.FirstName.Should().Be("Барсик");
        item.IsPet.Should().BeTrue();
        item.PetSpecies.Should().Be("кот");
    }

    [Fact]
    public async Task CreateAsync_NotFamilyMember_Forbidden()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var outsider = Db.AddUser();

        var (result, item) = await _sut.CreateAsync(
            family.Id, outsider.Id, new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот"));

        result.Should().Be(FamilyDependentAccessResult.Forbidden);
        item.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_IsPetFalse_ClearsPetSpeciesEvenIfSent()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var (_, item) = await _sut.CreateAsync(
            family.Id, admin.Id,
            new CreateFamilyDependentRequest("Ваня", "Иванов", null, Gender.Male, new DateOnly(2015, 3, 1), false, "кот"));

        item!.IsPet.Should().BeFalse();
        item.PetSpecies.Should().BeNull("вид животного не должен просочиться, если IsPet == false");
    }

    [Fact]
    public async Task UpdateAsync_Member_Succeeds()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var (_, created) = await _sut.CreateAsync(
            family.Id, admin.Id, new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот"));

        var result = await _sut.UpdateAsync(
            created!.Id, admin.Id,
            new UpdateFamilyDependentRequest("Барсик Второй", null, null, Gender.Male, null, true, "кот британский"));

        result.Should().Be(FamilyDependentAccessResult.Success);
        var updated = await Db.FamilyDependents.AsNoTracking().SingleAsync(d => d.Id == created.Id);
        updated.FirstName.Should().Be("Барсик Второй");
        updated.PetSpecies.Should().Be("кот британский");
    }

    [Fact]
    public async Task UpdateAsync_NotFamilyMember_Forbidden()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var outsider = Db.AddUser();
        var (_, created) = await _sut.CreateAsync(
            family.Id, admin.Id, new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот"));

        var result = await _sut.UpdateAsync(
            created!.Id, outsider.Id, new UpdateFamilyDependentRequest("Хакнуто", null, null, Gender.Male, null, true, "кот"));

        result.Should().Be(FamilyDependentAccessResult.Forbidden);
    }

    [Fact]
    public async Task DeleteAsync_Member_Forbidden_OnlyAdminMayDelete()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var member = Db.AddMember(family.Id);
        var (_, created) = await _sut.CreateAsync(
            family.Id, admin.Id, new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот"));

        var result = await _sut.DeleteAsync(created!.Id, member.Id);

        result.Should().Be(FamilyDependentAccessResult.Forbidden);
        (await Db.FamilyDependents.AsNoTracking().AnyAsync(d => d.Id == created.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_Admin_Succeeds_CascadesRecordsAndAttachments_AndDeletesBlobs()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var (_, dependent) = await _sut.CreateAsync(
            family.Id, admin.Id, new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот"));

        var record = new MedicalRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = admin.Id,
            Kind = MedicalRecordKind.Analysis,
            PersonName = "Барсик",
            RecordDate = new DateOnly(2024, 1, 1),
            FamilyDependentId = dependent!.Id,
            CreatedAt = DateTime.UtcNow,
        };
        Db.MedicalRecords.Add(record);
        Db.FileAttachments.Add(new FileAttachment
        {
            Id = Guid.NewGuid(),
            OwnerType = FileOwnerType.MedicalRecord,
            OwnerId = record.Id,
            StorageKey = "blobs/ab/cd/test-key",
            FileName = "scan.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            IsEncrypted = true,
            UploadedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var result = await _sut.DeleteAsync(dependent.Id, admin.Id);

        result.Should().Be(FamilyDependentAccessResult.Success);
        (await Db.FamilyDependents.AsNoTracking().AnyAsync(d => d.Id == dependent.Id)).Should().BeFalse();
        (await Db.MedicalRecords.AsNoTracking().AnyAsync(r => r.Id == record.Id)).Should().BeFalse();
        (await Db.FileAttachments.AsNoTracking().AnyAsync(a => a.OwnerId == record.Id)).Should().BeFalse();
        await _storage.Received(1).DeleteAsync("blobs/ab/cd/test-key", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_UnknownDependent_NotFound()
    {
        var admin = Db.AddUser();

        var result = await _sut.DeleteAsync(Guid.NewGuid(), admin.Id);

        result.Should().Be(FamilyDependentAccessResult.NotFound);
    }
}
