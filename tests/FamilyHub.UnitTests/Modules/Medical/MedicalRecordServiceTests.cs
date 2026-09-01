using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedicalRecordServiceTests : SqliteTestBase
{
    private readonly MedicalRecordService _sut;

    public MedicalRecordServiceTests()
    {
        _sut = new MedicalRecordService(
            Db, new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance),
            new TestSupport.RecordingDomainEventPublisher(),
            new FamilyHub.Infrastructure.Audit.MedicalAuditWriter(Db),
            new RussianTextSearcher(),
            Substitute.For<IFileStorage>(),
            NullLogger<MedicalRecordService>.Instance);
    }

    /// <summary>Обёртка над пагинированным GetVisibleRecordsAsync (UX-редизайн) — PageSize=100
    /// достаточен для всех сценариев этого файла (тестовые списки — единицы записей).</summary>
    private async Task<List<MedicalRecordDto>> GetRecordsAsync(Guid userId, MedicalRecordKind? kind = null)
    {
        var page = await _sut.GetVisibleRecordsAsync(userId, new MedicalRecordFilter(kind, PageSize: 100));
        return [.. page.Items];
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_Owner_AlwaysSeesOwnRecord()
    {
        var owner = Db.AddUser();
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(owner.Id));
        await Db.SaveChangesAsync();

        var result = await GetRecordsAsync(owner.Id);

        result.Should().ContainSingle();
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_NotSharedYet_OtherFamilyMemberDoesNotSeeIt()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        var (family, _) = Db.SeedFamilyWithAdmin();
        var familyMate = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();

        var result = await GetRecordsAsync(familyMate.Id);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_SharedAndActiveMember_Sees()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        var familyMate = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();

        var shareResult = await _sut.ShareWithFamilyAsync(owner.Id, family.Id);

        shareResult.Should().Be(MedicalRecordAccessResult.Success);
        var result = await GetRecordsAsync(familyMate.Id);
        result.Should().ContainSingle(r => r.Id == record.Id);
    }

    [Fact]
    public async Task ShareWithFamilyAsync_OwnerNotMemberOfThatFamily_Forbidden()
    {
        var owner = Db.AddUser();
        var (family, _) = Db.SeedFamilyWithAdmin();

        var result = await _sut.ShareWithFamilyAsync(owner.Id, family.Id);

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_PendingApprovalMember_DoesNotSeeSharedRecord()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        Db.FamilyMedicalShares.Add(new FamilyMedicalShare
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            FamilyId = family.Id,
            SharedAt = DateTime.UtcNow,
        });
        var pending = Db.AddMember(family.Id, FamilyRole.Member, MemberStatus.PendingApproval);
        await Db.SaveChangesAsync();

        var result = await GetRecordsAsync(pending.Id);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_HiddenFromSpecificFamily_NotVisibleToThatFamilyButVisibleToOwner()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        Db.FamilyMedicalShares.Add(new FamilyMedicalShare
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            FamilyId = family.Id,
            SharedAt = DateTime.UtcNow,
        });
        var familyMate = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();

        var hideResult = await _sut.HideFromFamiliesAsync(owner.Id, record.Id, [family.Id]);

        hideResult.Should().Be(MedicalRecordAccessResult.Success);
        (await GetRecordsAsync(familyMate.Id)).Should().BeEmpty();
        (await GetRecordsAsync(owner.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task HideFromFamiliesAsync_NotOwner_ForbiddenEvenForFamilyAdmin()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();

        var result = await _sut.HideFromFamiliesAsync(admin.Id, record.Id, [family.Id]);

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
    }

    [Fact]
    public async Task UnhideFromFamiliesAsync_RestoresVisibility()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        Db.FamilyMedicalShares.Add(new FamilyMedicalShare
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            FamilyId = family.Id,
            SharedAt = DateTime.UtcNow,
        });
        var familyMate = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();
        await _sut.HideFromFamiliesAsync(owner.Id, record.Id, [family.Id]);

        var result = await _sut.UnhideFromFamiliesAsync(owner.Id, record.Id, [family.Id]);

        result.Should().Be(MedicalRecordAccessResult.Success);
        (await GetRecordsAsync(familyMate.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task UnshareFamilyAsync_DoesNotClearExistingHiddenMarkers()
    {
        // Инвариант 5: Unshare не чистит MedicalRecordHidden — повторный Share вернёт то же скрытие.
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();
        await _sut.ShareWithFamilyAsync(owner.Id, family.Id);
        await _sut.HideFromFamiliesAsync(owner.Id, record.Id, [family.Id]);

        var unshareResult = await _sut.UnshareFamilyAsync(owner.Id, family.Id);
        unshareResult.Should().Be(MedicalRecordAccessResult.Success);
        var reshareResult = await _sut.ShareWithFamilyAsync(owner.Id, family.Id);
        reshareResult.Should().Be(MedicalRecordAccessResult.Success);

        var familyMate = Db.AddMember(family.Id);
        (await GetRecordsAsync(familyMate.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_HideFromFamilyIds_AppliesTripleIntersection()
    {
        // Запрошено скрыть от семьи, в которой владелец не состоит активным членом — не должно попасть.
        var (myFamily, _) = Db.SeedFamilyWithAdmin("Mine");
        var owner = Db.AddMember(myFamily.Id);
        var (otherFamily, _) = Db.SeedFamilyWithAdmin("Other");

        Db.FamilyMedicalShares.Add(new FamilyMedicalShare
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            FamilyId = myFamily.Id,
            SharedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var (_, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            new DateOnly(2024, 1, 1), null, null, [myFamily.Id, otherFamily.Id]));

        var hidden = Db.MedicalRecordHiddens.Where(h => h.MedicalRecordId == dto!.Id).Select(h => h.FamilyId).ToList();
        hidden.Should().ContainSingle().Which.Should().Be(myFamily.Id);
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_FilterByKind_OnlyReturnsMatchingKind()
    {
        var owner = Db.AddUser();
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.Analysis));
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.DoctorVisit));
        await Db.SaveChangesAsync();

        var analyses = await GetRecordsAsync(owner.Id, MedicalRecordKind.Analysis);
        var visits = await GetRecordsAsync(owner.Id, MedicalRecordKind.DoctorVisit);
        var all = await GetRecordsAsync(owner.Id);

        analyses.Should().ContainSingle(r => r.Kind == MedicalRecordKind.Analysis);
        visits.Should().ContainSingle(r => r.Kind == MedicalRecordKind.DoctorVisit);
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_FilterByKind_DoesNotMatchOtherKind()
    {
        // types=visit не должен находить (и, следовательно, расшифровывать) анализы, и наоборот —
        // ключевая гарантия для SearchService.SearchMedicalRecordsAsync.
        var owner = Db.AddUser();
        var analysis = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.Analysis);
        analysis.Doctor = "Иванов";
        var visit = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.DoctorVisit);
        visit.Doctor = "Иванов";
        Db.MedicalRecords.AddRange(analysis, visit);
        await Db.SaveChangesAsync();

        var visitHits = await _sut.SearchAsync(owner.Id, "Иванов", MedicalRecordKind.DoctorVisit);
        var analysisHits = await _sut.SearchAsync(owner.Id, "Иванов", MedicalRecordKind.Analysis);
        var allHits = await _sut.SearchAsync(owner.Id, "Иванов");

        visitHits.Should().ContainSingle(h => h.Record.Id == visit.Id);
        analysisHits.Should().ContainSingle(h => h.Record.Id == analysis.Id);
        allHits.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateAsync_SetsKindFromRequest()
    {
        var owner = Db.AddUser();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            new DateOnly(2024, 1, 1), "Доктор", null, null, MedicalRecordKind.DoctorVisit));

        result.Should().Be(MedicalRecordAccessResult.Success);
        dto!.Kind.Should().Be(MedicalRecordKind.DoctorVisit);
        (await Db.MedicalRecords.FindAsync(dto.Id))!.Kind.Should().Be(MedicalRecordKind.DoctorVisit);
    }

    [Fact]
    public async Task CreateAsync_BothDependentAndTargetSet_ReturnsInvalidTarget()
    {
        var owner = Db.AddUser();
        var (family, _) = Db.SeedFamilyWithAdmin();
        var dependent = new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = family.Id, FirstName = "Барсик", IsPet = true, Gender = Gender.Male, CreatedByUserId = owner.Id,
            CreatedAt = DateTime.UtcNow,
        };
        Db.FamilyDependents.Add(dependent);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            new DateOnly(2024, 1, 1), null, null, null,
            FamilyDependentId: dependent.Id, TargetUserId: Guid.NewGuid()));

        result.Should().Be(MedicalRecordAccessResult.InvalidTarget);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ForDependent_MemberOfDependentFamily_Succeeds_AndVisibleToOtherActiveMember()
    {
        var (family, owner) = Db.SeedFamilyWithAdmin();
        var otherMember = Db.AddMember(family.Id);
        var dependent = new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = family.Id, FirstName = "Барсик", IsPet = true, Gender = Gender.Male, CreatedByUserId = owner.Id,
            CreatedAt = DateTime.UtcNow,
        };
        Db.FamilyDependents.Add(dependent);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            new DateOnly(2024, 1, 1), "Ветеринар", null, null, FamilyDependentId: dependent.Id));

        result.Should().Be(MedicalRecordAccessResult.Success);
        dto!.OwnerUserId.Should().Be(owner.Id, "владелец — тот, кто физически загрузил, а не подопечный");
        (await GetRecordsAsync(otherMember.Id)).Should().ContainSingle(r => r.Id == dto.Id);
    }

    [Fact]
    public async Task CreateAsync_ForDependentOfAnotherFamily_ReturnsForbidden()
    {
        var owner = Db.AddUser();
        var (otherFamily, otherAdmin) = Db.SeedFamilyWithAdmin();
        var dependent = new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = otherFamily.Id, FirstName = "Чужой", IsPet = false, Gender = Gender.Male, CreatedByUserId = otherAdmin.Id,
            CreatedAt = DateTime.UtcNow,
        };
        Db.FamilyDependents.Add(dependent);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            new DateOnly(2024, 1, 1), null, null, null, FamilyDependentId: dependent.Id));

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ForTargetUserWithoutSharedFamily_ReturnsForbidden()
    {
        var owner = Db.AddUser();
        var stranger = Db.AddUser();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            new DateOnly(2024, 1, 1), null, null, null, TargetUserId: stranger.Id));

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ForTargetUserInSameFamily_Succeeds_AndVisibleToTarget_ButOwnerStaysUploader()
    {
        var (family, owner) = Db.SeedFamilyWithAdmin();
        var target = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            new DateOnly(2024, 1, 1), null, null, null, TargetUserId: target.Id));

        result.Should().Be(MedicalRecordAccessResult.Success);
        dto!.OwnerUserId.Should().Be(owner.Id);
        dto.TargetUserId.Should().Be(target.Id);
        (await GetRecordsAsync(target.Id)).Should().ContainSingle(r => r.Id == dto.Id);
    }

    [Fact]
    public async Task DeleteAsync_Owner_Succeeds_AndRemovesRecord()
    {
        var owner = Db.AddUser();
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(owner.Id));
        await Db.SaveChangesAsync();
        var record = await Db.MedicalRecords.FirstAsync(r => r.OwnerUserId == owner.Id);

        var result = await _sut.DeleteAsync(owner.Id, record.Id);

        result.Should().Be(MedicalRecordAccessResult.Success);
        // ExecuteDeleteAsync — bulk-операция в обход change tracker'а: FindAsync вернул бы
        // устаревший закэшированный экземпляр (record уже отслеживается тем же Db-контекстом
        // после чтения выше) вместо реального состояния БД — нужен AsNoTracking, чтобы форсировать
        // настоящий запрос.
        (await Db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == record.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_TargetUser_CannotDelete_UnconditionalOwnerOnlyRule()
    {
        var (family, owner) = Db.SeedFamilyWithAdmin();
        var target = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();
        var (_, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            new DateOnly(2024, 1, 1), null, null, null, TargetUserId: target.Id));

        var result = await _sut.DeleteAsync(target.Id, dto!.Id);

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
        (await Db.MedicalRecords.FindAsync(dto.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownRecord_ReturnsNotFound()
    {
        var owner = Db.AddUser();

        var result = await _sut.DeleteAsync(owner.Id, Guid.NewGuid());

        result.Should().Be(MedicalRecordAccessResult.NotFound);
    }

    // --- UX-редизайн: сортировка/пагинация/фильтры ---

    [Fact]
    public async Task GetVisibleRecordsAsync_OrdersByRecordDateDescending_NotByCreatedAt()
    {
        var owner = Db.AddUser();
        var older = TestData.NewMedicalRecord(owner.Id);
        older.RecordDate = new DateOnly(2024, 1, 1);
        older.CreatedAt = DateTime.UtcNow; // создана позже, но дата анализа раньше
        var newer = TestData.NewMedicalRecord(owner.Id);
        newer.RecordDate = new DateOnly(2025, 6, 1);
        newer.CreatedAt = DateTime.UtcNow.AddDays(-30);
        Db.MedicalRecords.AddRange(older, newer);
        await Db.SaveChangesAsync();

        var page = await _sut.GetVisibleRecordsAsync(owner.Id, new MedicalRecordFilter(PageSize: 100));

        page.Items.Select(r => r.Id).Should().Equal(newer.Id, older.Id);
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_Pagination_SplitsIntoPagesWithCorrectTotals()
    {
        var owner = Db.AddUser();
        for (var i = 0; i < 23; i++)
        {
            var record = TestData.NewMedicalRecord(owner.Id);
            record.RecordDate = new DateOnly(2024, 1, 1).AddDays(i);
            Db.MedicalRecords.Add(record);
        }
        await Db.SaveChangesAsync();

        var page1 = await _sut.GetVisibleRecordsAsync(owner.Id, new MedicalRecordFilter(Page: 1, PageSize: 15));
        var page2 = await _sut.GetVisibleRecordsAsync(owner.Id, new MedicalRecordFilter(Page: 2, PageSize: 15));

        page1.TotalCount.Should().Be(23);
        page1.TotalPages.Should().Be(2);
        page1.Items.Should().HaveCount(15);
        page2.Items.Should().HaveCount(8);
        page1.Items.Select(r => r.Id).Should().NotIntersectWith(page2.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_FilterByDateRange_ExcludesOutOfRange()
    {
        var owner = Db.AddUser();
        var inRange = TestData.NewMedicalRecord(owner.Id);
        inRange.RecordDate = new DateOnly(2024, 6, 15);
        var before = TestData.NewMedicalRecord(owner.Id);
        before.RecordDate = new DateOnly(2024, 1, 1);
        var after = TestData.NewMedicalRecord(owner.Id);
        after.RecordDate = new DateOnly(2024, 12, 31);
        Db.MedicalRecords.AddRange(inRange, before, after);
        await Db.SaveChangesAsync();

        var page = await _sut.GetVisibleRecordsAsync(
            owner.Id, new MedicalRecordFilter(From: new DateOnly(2024, 3, 1), To: new DateOnly(2024, 9, 1), PageSize: 100));

        page.Items.Should().ContainSingle(r => r.Id == inRange.Id);
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_FilterByDoctor_MatchesCaseInsensitiveSubstring_InMemoryPath()
    {
        var owner = Db.AddUser();
        var petrov = TestData.NewMedicalRecord(owner.Id);
        petrov.Doctor = "Кардиолог Петрова";
        var ivanov = TestData.NewMedicalRecord(owner.Id);
        ivanov.Doctor = "Терапевт Иванов";
        Db.MedicalRecords.AddRange(petrov, ivanov);
        await Db.SaveChangesAsync();

        var page = await _sut.GetVisibleRecordsAsync(owner.Id, new MedicalRecordFilter(Doctor: "петров", PageSize: 100));

        page.Items.Should().ContainSingle(r => r.Id == petrov.Id);
        page.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_AttachmentAndIndicatorCounts_ReflectActualData()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();
        Db.FileAttachments.Add(new FileAttachment
        {
            Id = Guid.NewGuid(), OwnerType = FileOwnerType.MedicalRecord, OwnerId = record.Id,
            StorageKey = "k1", FileName = "a.pdf", ContentType = "application/pdf", SizeBytes = 1,
            UploadedAt = DateTime.UtcNow, ExtractedAt = null,
        });
        Db.FileAttachments.Add(new FileAttachment
        {
            Id = Guid.NewGuid(), OwnerType = FileOwnerType.MedicalRecord, OwnerId = record.Id,
            StorageKey = "k2", FileName = "b.pdf", ContentType = "application/pdf", SizeBytes = 1,
            UploadedAt = DateTime.UtcNow, ExtractedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var page = await _sut.GetVisibleRecordsAsync(owner.Id, new MedicalRecordFilter(PageSize: 100));

        var dto = page.Items.Should().ContainSingle(r => r.Id == record.Id).Which;
        dto.AttachmentCount.Should().Be(2);
        dto.UnrecognizedAttachmentCount.Should().Be(1);
    }

    /// <summary>Редизайн v2 — AbnormalIndicatorCount/NormalIndicatorCount на карточке списка
    /// («2 вне нормы»/«12 в норме»): тот же GroupBy, что и уже существующий IndicatorCount,
    /// доп. Count() по Flag. Critical и High/Low оба считаются "вне нормы", Unknown — ни туда,
    /// ни туда ("без нормы" на фронте = IndicatorCount − Abnormal − Normal).</summary>
    [Fact]
    public async Task GetVisibleRecordsAsync_IndicatorCounts_SplitByFlag()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        Db.LabIndicators.AddRange(
            NewIndicator(record, owner.Id, IndicatorFlag.High),
            NewIndicator(record, owner.Id, IndicatorFlag.Critical),
            NewIndicator(record, owner.Id, IndicatorFlag.Normal),
            NewIndicator(record, owner.Id, IndicatorFlag.Unknown));
        await Db.SaveChangesAsync();

        var page = await _sut.GetVisibleRecordsAsync(owner.Id, new MedicalRecordFilter(PageSize: 100));

        var dto = page.Items.Should().ContainSingle(r => r.Id == record.Id).Which;
        dto.IndicatorCount.Should().Be(4);
        dto.AbnormalIndicatorCount.Should().Be(2, "High и Critical оба считаются отклонением");
        dto.NormalIndicatorCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_SetsTitle()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.UpdateAsync(
            owner.Id, record.Id, new UpdateMedicalRecordRequest(record.RecordDate, null, null, "Общий анализ крови"));

        result.Should().Be(MedicalRecordAccessResult.Success);
        dto!.Title.Should().Be("Общий анализ крови");
    }

    /// <summary>Title (редизайн v3, PR7) — та же семантика, что Doctor/Description: форма всегда
    /// шлёт текущее значение, пустая строка явно очищает ранее выставленное распознаванием
    /// название, а не оставляет его нетронутым.</summary>
    [Fact]
    public async Task UpdateAsync_BlankTitle_ClearsPreviouslyRecognizedTitle()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        record.Title = "Распознанное название";
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.UpdateAsync(
            owner.Id, record.Id, new UpdateMedicalRecordRequest(record.RecordDate, null, null, "  "));

        result.Should().Be(MedicalRecordAccessResult.Success);
        dto!.Title.Should().BeNull();
    }

    private static LabIndicator NewIndicator(MedicalRecord record, Guid ownerUserId, IndicatorFlag flag) => new()
    {
        Id = Guid.NewGuid(),
        MedicalRecordId = record.Id,
        RecordDate = record.RecordDate,
        OwnerUserId = ownerUserId,
        AnalyteKey = $"analyte-{Guid.NewGuid():N}",
        DisplayName = "Тестовый показатель",
        Flag = flag,
        RefSource = RefSource.Blank,
        Specimen = SpecimenType.Blood,
        Position = 0,
        ValueRaw = "1",
        CreatedAt = DateTime.UtcNow,
    };
}
