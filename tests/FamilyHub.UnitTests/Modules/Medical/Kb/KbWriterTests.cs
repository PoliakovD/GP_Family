using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Kb;

/// <summary>
/// KbWriter — единственный писатель в kb.global_medications_kb (тот самый «writer-сервис этапа 4»,
/// которого явно ждёт Kb/KbIsolationGuardTests.PersonalContext_CannotBeStoredInKbRow). Этот файл
/// покрывает только путь ОТКАЗА записи (значения payload похожи на персональный контекст) — он не
/// доходит до raw SQL (проверка выполняется до похода в БД), поэтому безопасен на SQLite. Успешный
/// upsert (ON CONFLICT, raw SQL к text[]/jsonb) — Postgres-специфичен, покрывается интеграционными
/// тестами (EnrichmentPipelineTests), не юнитом.
/// </summary>
public class KbWriterTests : SqliteTestBase
{
    private readonly KbWriter _sut;

    public KbWriterTests()
    {
        _sut = new KbWriter(Db, NullLogger<KbWriter>.Instance);
    }

    private static MedicationSummary SummaryWithNote(string specialNotes) =>
        new("Ибупрофен", ["Нурофен"], "таблетки", "жаропонижающее", "сбивает температуру и снимает боль",
            "по 1 таб. до 3 раз в сутки", "в сухом месте", "не влияет", specialNotes, [0]);

    private static MedicationSummary SummaryWithUsage(string usage) =>
        new("Ибупрофен", ["Нурофен"], "таблетки", "жаропонижающее", "сбивает температуру и снимает боль",
            usage, "в сухом месте", "не влияет", null, [0]);

    [Fact]
    public async Task Upsert_ValueLooksLikeGuid_IsRejected()
    {
        var summary = SummaryWithNote("См. запись 3fa85f64-5717-4562-b3fc-2c963f66afa6 в системе");

        var result = await _sut.UpsertAsync("ибупрофен", "Ибупрофен", summary, "тест");

        result.Success.Should().BeFalse("GUID в тексте — признак утёкшего персонального идентификатора");
        result.RejectionReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Upsert_ValueLooksLikeEmail_IsRejected()
    {
        var summary = SummaryWithNote("Уточнить у ivan.petrov@example.com");

        var result = await _sut.UpsertAsync("ибупрофен", "Ибупрофен", summary, "тест");

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Upsert_LongDigitSequence_IsRejected()
    {
        var summary = SummaryWithNote("Телефон горячей линии 89261234567");

        var result = await _sut.UpsertAsync("ибупрофен", "Ибупрофен", summary, "тест");

        result.Success.Should().BeFalse("длинная цифровая последовательность похожа на телефон/паспорт, не на знание о препарате");
    }

    [Theory]
    [InlineData("Пациент с UserId в анамнезе")]
    [InlineData("Обсуждение в Telegram-чате семьи")]
    public async Task Upsert_PersonalKeywordInText_IsRejected(string suspiciousNote)
    {
        var summary = SummaryWithNote(suspiciousNote);

        var result = await _sut.UpsertAsync("ибупрофен", "Ибупрофен", summary, "тест");

        result.Success.Should().BeFalse();
        result.RejectionReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Upsert_PersonalKeywordInUsage_IsRejected()
    {
        // Usage — новое поле (общий способ применения из инструкции, этап 4 «не ограничивай
        // модель») — должно проходить ту же проверку на персональный контекст, что и остальные.
        var summary = SummaryWithUsage("Согласовано с Telegram-ботом семьи");

        var result = await _sut.UpsertAsync("ибупрофен", "Ибупрофен", summary, "тест");

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Upsert_PersonalKeywordInExtraAlias_IsRejected()
    {
        // extraAliases — исходное искажённое OCR название при переименовании (см.
        // MedicationEnrichmentProcessor.ResolveCorrectedName) — тоже проходит проверку.
        var summary = SummaryWithNote("Хранить в сухом месте");

        var result = await _sut.UpsertAsync(
            "ибупрофен", "Ибупрофен", summary, "тест", extraAliases: ["ivan.petrov@example.com"]);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Upsert_ChecksDisplayNameToo_NotOnlySummaryFields()
    {
        var summary = SummaryWithNote("Хранить в сухом месте");

        // Подозрительное значение в displayName (аргумент вызова, не в самом MedicationSummary).
        var result = await _sut.UpsertAsync("ибупрофен", "ivan.petrov@example.com", summary, "тест");

        result.Success.Should().BeFalse("проверка на персональный контекст должна покрывать все текстовые поля записи, включая DisplayName");
    }
}
