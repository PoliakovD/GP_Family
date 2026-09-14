using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.LmStudio;

/// <summary>
/// Позиция в ОБЩЕЙ очереди к единственному локальному LLM (найденный на живом отчёте баг:
/// "Распознать" был верно блокирован, но бейдж стадии — "Читаем текст" — создавал впечатление
/// активной работы, хотя задача на деле просто ждала своей очереди позади большого потока задач
/// обогащения справочника). Проверяет, что счёт идёт по ВСЕМ четырём таблицам задач, не только по
/// своей, и что Completed/Failed не считаются "впереди".
/// </summary>
public class LlmQueuePositionServiceTests : SqliteTestBase
{
    private readonly LlmQueuePositionService _sut;

    public LlmQueuePositionServiceTests()
    {
        _sut = new LlmQueuePositionService(Db);
    }

    private static readonly Guid TestUserId = Guid.NewGuid();

    [Fact]
    public async Task GetQueueAheadAsync_NoActiveJobsAnywhere_ReturnsZero()
    {
        var result = await _sut.GetQueueAheadAsync(DateTime.UtcNow);

        result.Should().Be(0);
    }

    [Fact]
    public async Task GetQueueAheadAsync_CountsAcrossAllFourTables_NotOnlyOwnTable()
    {
        var baseline = DateTime.UtcNow;

        // Один Pending в каждой из четырёх таблиц, все раньше "моей" задачи — старый расчёт (см.
        // ExtractionQueryService до этого фикса) считал бы только MedicalDocumentExtractionJobs
        // и вернул бы 1 вместо 4.
        Db.MedicalDocumentExtractionJobs.Add(NewExtractionJob(baseline.AddMinutes(-4), EnrichmentJobStatus.Pending));
        Db.LabAnalyteEnrichmentJobs.Add(NewLabAnalyteJob(baseline.AddMinutes(-3), EnrichmentJobStatus.Pending));
        Db.MedicationEnrichmentJobs.Add(NewMedicationJob(baseline.AddMinutes(-2), EnrichmentJobStatus.Running));
        Db.VisitMedicationEnrichmentJobs.Add(NewVisitMedicationJob(baseline.AddMinutes(-1), EnrichmentJobStatus.Running));
        await Db.SaveChangesAsync();

        var result = await _sut.GetQueueAheadAsync(baseline);

        result.Should().Be(4, "все четыре задачи старше и активны — реальная очередь к общей модели, не только своя таблица");
    }

    [Fact]
    public async Task GetQueueAheadAsync_IgnoresCompletedAndFailedJobs()
    {
        var baseline = DateTime.UtcNow;

        Db.LabAnalyteEnrichmentJobs.Add(NewLabAnalyteJob(baseline.AddMinutes(-2), EnrichmentJobStatus.Completed));
        Db.MedicationEnrichmentJobs.Add(NewMedicationJob(baseline.AddMinutes(-1), EnrichmentJobStatus.Failed));
        await Db.SaveChangesAsync();

        var result = await _sut.GetQueueAheadAsync(baseline);

        result.Should().Be(0, "завершённые/упавшие задачи больше не соревнуются за гейт LM Studio");
    }

    [Fact]
    public async Task GetQueueAheadAsync_IgnoresJobsCreatedLater()
    {
        var baseline = DateTime.UtcNow;

        Db.LabAnalyteEnrichmentJobs.Add(NewLabAnalyteJob(baseline.AddMinutes(1), EnrichmentJobStatus.Pending));
        await Db.SaveChangesAsync();

        var result = await _sut.GetQueueAheadAsync(baseline);

        result.Should().Be(0, "задача, созданная ПОЗЖЕ, не стоит впереди в очереди");
    }

    [Fact]
    public async Task CountAhead_PureFunction_MatchesManualCount()
    {
        var now = DateTime.UtcNow;
        var timestamps = new List<DateTime> { now.AddMinutes(-5), now.AddMinutes(-3), now.AddMinutes(-1), now.AddMinutes(2) };

        LlmQueuePositionService.CountAhead(timestamps, now).Should().Be(3);
    }

    private static MedicalDocumentExtractionJob NewExtractionJob(DateTime createdAt, EnrichmentJobStatus status) => new()
    {
        Id = Guid.NewGuid(), MedicalRecordId = Guid.NewGuid(), RequestedByUserId = TestUserId,
        Status = status, CreatedAt = createdAt,
    };

    private static LabAnalyteEnrichmentJob NewLabAnalyteJob(DateTime createdAt, EnrichmentJobStatus status) => new()
    {
        Id = Guid.NewGuid(), NormalizedName = "тест", SpecimenKbId = Guid.NewGuid(), SourceDisplayName = "Тест",
        RequestedByUserId = TestUserId, Status = status, CreatedAt = createdAt,
    };

    private static MedicationEnrichmentJob NewMedicationJob(DateTime createdAt, EnrichmentJobStatus status) => new()
    {
        Id = Guid.NewGuid(), NormalizedName = "тест", SourceDisplayName = "Тест", FamilyId = Guid.NewGuid(),
        RequestedByUserId = TestUserId, Status = status, CreatedAt = createdAt,
    };

    private static VisitMedicationEnrichmentJob NewVisitMedicationJob(DateTime createdAt, EnrichmentJobStatus status) => new()
    {
        Id = Guid.NewGuid(), NormalizedName = "тест", SourceDisplayName = "Тест",
        RequestedByUserId = TestUserId, Status = status, CreatedAt = createdAt,
    };
}
