using System.Text.Json;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Уточнение родового названия показателя (см. class doc AnalyteSubjectResolver) — та же форма
/// "модель предлагает, детерминированный код ветирует", что SpecimenResolver/OcrNameCorrector,
/// плюс дополнительный антигаллюцинационный гейт: субъект обязан реально встречаться в тексте
/// документа (аналог гейта LmStudioMedicalDocumentExtractor для самих показателей). Оба вето — на
/// реальном IRussianTextSearcher (морфология, не дословное совпадение строки) — тот же сервис, что
/// в проде, не заглушка: живой пример (протокол лаборатории на посев кала на сальмонеллу) показал,
/// что бланк склоняет название ("рода сальмонелла"), а модель называет субъект литературно
/// ("Сальмонеллы") — дословное сравнение отклоняло верный ответ.
/// </summary>
public class AnalyteSubjectResolverTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly AnalyteSubjectResolver _sut;

    public AnalyteSubjectResolverTests()
    {
        _sut = new AnalyteSubjectResolver(
            _client, new RussianTextSearcher(), TestPromptProvider.ReturningFallback(), NullLogger<AnalyteSubjectResolver>.Instance);
    }

    private static DocumentContent TextContent(string text) => DocumentContent.FromText(text);

    private void SetUpModelResponse(string? subject, string? rawLabel, string? evidence, double confidence) =>
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new { subject, rawLabel, evidence, confidence })), null));

    /// <summary>Живой пример (реальный протокол лаборатории, присланный пользователем) — таблица
    /// печатает родовое "Бактериальный микроорганизм... не обнаружены", конкретный микроорганизм
    /// назван только в "Оказанные услуги" ОТДЕЛЬНОЙ, СКЛОНЯЕМОЙ формой слова ("рода сальмонелла",
    /// не "сальмонеллы"), а rawLabel — вся фраза услуги целиком, не короткое понятие.</summary>
    private const string RealWorldDocumentTail = """
        Результаты проведенных исследований
        Дата Показатель Значение
        Отдельные лабораторные тесты
        28.05.2026 08:21 Бактериальный микроорганизм, концентрация в условных единицах в кале
        культуральным методом не обнаружены

        Оказанные услуги
        A26.19.003 Микробиологическое (культуральное) исследование фекалий/ректального мазка на
        микроорганизмы рода сальмонелла (Salmonella spp.) от 28.05.2026
        """;

    [Fact]
    public async Task ResolveAsync_RealWorldReport_InflectedSubjectInLongServiceLine_ResolvesDespiteMorphologyMismatch()
    {
        // Модель называет субъект литературно ("Сальмонеллы"), бланк — в другом падеже/числе внутри
        // длинной фразы услуги ("рода сальмонелла") — ни дословное Contains, ни триграммы строки
        // целиком это не поймали бы (см. class doc), IRussianTextSearcher обязан.
        SetUpModelResponse(
            "Сальмонеллы",
            "Микробиологическое (культуральное) исследование фекалий/ректального мазка на микроорганизмы рода сальмонелла (Salmonella spp.)",
            "на микроорганизмы рода сальмонелла (Salmonella spp.)", 0.95);

        var result = await _sut.ResolveAsync(TextContent(RealWorldDocumentTail), ["Бактериальный микроорганизм"]);

        result.Subject.Should().Be("Сальмонеллы");
    }

    [Fact]
    public async Task ResolveAsync_ConfidentSubjectPresentInDocument_ReturnsSubject()
    {
        SetUpModelResponse("Сальмонеллы", "посев на сальмонеллы", "Оказанные услуги: посев на сальмонеллы", 0.9);

        var result = await _sut.ResolveAsync(
            TextContent("Оказанные услуги: посев на сальмонеллы\nБактериальные микроорганизмы - не выявлено"),
            ["Бактериальные микроорганизмы"]);

        result.Subject.Should().Be("Сальмонеллы");
    }

    [Fact]
    public async Task ResolveAsync_LowConfidence_ReturnsEmpty()
    {
        // confidence ниже MinConfidence (0.7) должен отклонять субъект, даже правдоподобный.
        SetUpModelResponse("Сальмонеллы", "посев на сальмонеллы", "...", 0.5);

        var result = await _sut.ResolveAsync(
            TextContent("Оказанные услуги: посев на сальмонеллы\nБактериальные микроорганизмы - не выявлено"),
            ["Бактериальные микроорганизмы"]);

        result.Should().Be(AnalyteSubjectResolution.Empty);
    }

    [Fact]
    public async Task ResolveAsync_SubjectUnrelatedToRawLabel_VetoesResolution()
    {
        // Модель предложила subject, никак не связанный с тем, что реально написано (rawLabel) —
        // вето по релевантности должно отклонить, даже при высокой заявленной confidence.
        SetUpModelResponse("Сальмонеллы", "общий анализ крови", "...", 0.95);

        var result = await _sut.ResolveAsync(
            TextContent("Оказанные услуги: посев на сальмонеллы\nОбщий анализ крови"),
            ["Бактериальные микроорганизмы"]);

        result.Should().Be(AnalyteSubjectResolution.Empty,
            "модель не должна была подменить понятие — низкая релевантность subject/rawLabel это ловит");
    }

    [Fact]
    public async Task ResolveAsync_SubjectNotInDocumentText_VetoesAsHallucination()
    {
        // rawLabel совпадает с subject дословно, но ни то, ни другое реально не встречается в
        // тексте документа — модель придумала объект исследования.
        SetUpModelResponse("Лямблии", "лямблии", "...", 0.9);

        var result = await _sut.ResolveAsync(
            TextContent("Общий анализ мочи\nЦвет: соломенно-жёлтый\nПрозрачность: полная"),
            ["Цвет"]);

        result.Should().Be(AnalyteSubjectResolution.Empty);
    }

    [Fact]
    public async Task ResolveAsync_ModelUnavailable_ReturnsEmpty_DoesNotThrow()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LmStudioJsonResult.Failure("недоступен"));

        var result = await _sut.ResolveAsync(TextContent("любой текст документа"), ["показатель"]);

        result.Should().Be(AnalyteSubjectResolution.Empty);
    }

    [Fact]
    public async Task ResolveAsync_EmptyDocument_ReturnsEmpty_DoesNotCallModel()
    {
        var result = await _sut.ResolveAsync(DocumentContent.FromText(""), ["показатель"]);

        result.Should().Be(AnalyteSubjectResolution.Empty);
        await _client.DidNotReceiveWithAnyArgs().ExtractJsonAsync(default!, default!, Arg.Any<CancellationToken>());
    }
}
