using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Search;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

/// <summary>
/// SearchService — анализы/врачи (SearchMedicalRecordsAsync). Лекарства и справочник намеренно НЕ
/// покрыты здесь: те источники строятся на raw Postgres SQL (tsvector/pg_trgm), недоступном под
/// SQLite (см. SqliteTestBase) — их покрывает SearchApiTests в IntegrationTests. Тесты ниже никогда
/// не запрашивают Medication/Kb/Birthday, поэтому SearchService их вообще не трогает.
/// </summary>
public class SearchServiceTests : SqliteTestBase
{
    private readonly SearchService _sut;

    public SearchServiceTests()
    {
        var access = new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance);
        var medicalRecords = new MedicalRecordService(
            Db, access, new TestSupport.RecordingDomainEventPublisher(),
            new FamilyHub.Infrastructure.Audit.MedicalAuditWriter(Db),
            new RussianTextSearcher(), Substitute.For<IFileStorage>(), NullLogger<MedicalRecordService>.Instance);
        var birthdays = Substitute.For<IBirthdaySearchSource>();
        birthdays.SearchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _sut = new SearchService(Db, access, medicalRecords, birthdays);
    }

    [Fact]
    public async Task SearchAsync_TypesRecordAndVisit_MapsEachHitToItsOwnResultType()
    {
        var owner = Db.AddUser();
        var analysis = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.Analysis);
        analysis.Doctor = "Иванов";
        var visit = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.DoctorVisit);
        visit.Doctor = "Иванов";
        Db.MedicalRecords.AddRange(analysis, visit);
        await Db.SaveChangesAsync();

        var response = await _sut.SearchAsync(
            owner.Id, "Иванов", new HashSet<SearchResultType> { SearchResultType.Record, SearchResultType.Visit });

        response.Items.Should().HaveCount(2);
        response.Items.Should().ContainSingle(i => i.Id == analysis.Id && i.Type == SearchResultType.Record);
        response.Items.Should().ContainSingle(i => i.Id == visit.Id && i.Type == SearchResultType.Visit);
    }

    [Fact]
    public async Task SearchAsync_TypesRecordOnly_DoesNotReturnMatchingVisit()
    {
        var owner = Db.AddUser();
        var visit = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.DoctorVisit);
        visit.Doctor = "Петров";
        Db.MedicalRecords.Add(visit);
        await Db.SaveChangesAsync();

        var response = await _sut.SearchAsync(
            owner.Id, "Петров", new HashSet<SearchResultType> { SearchResultType.Record });

        response.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_TypesVisitOnly_DoesNotReturnMatchingAnalysis()
    {
        var owner = Db.AddUser();
        var analysis = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.Analysis);
        analysis.Doctor = "Сидоров";
        Db.MedicalRecords.Add(analysis);
        await Db.SaveChangesAsync();

        var response = await _sut.SearchAsync(
            owner.Id, "Сидоров", new HashSet<SearchResultType> { SearchResultType.Visit });

        response.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_DoctorVisitHit_TitleBuiltAroundDoctorName()
    {
        var owner = Db.AddUser();
        var visit = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.DoctorVisit);
        visit.Doctor = "Кардиолог Петрова";
        Db.MedicalRecords.Add(visit);
        await Db.SaveChangesAsync();

        var response = await _sut.SearchAsync(
            owner.Id, "Петрова", new HashSet<SearchResultType> { SearchResultType.Visit });

        // PersonName убран (v2) — self-запись резолвится из профиля владельца
        // (TestData.NewUser() сеет LastName="Testov", FirstName="Test" по умолчанию).
        response.Items.Should().ContainSingle().Which.Title.Should().Be("Testov Test · Кардиолог Петрова");
    }
}
