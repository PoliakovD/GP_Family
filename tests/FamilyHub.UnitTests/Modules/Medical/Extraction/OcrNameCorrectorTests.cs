using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Второй проход коррекции OCR-имён (см. class doc OcrNameCorrector) — приём "модель
/// предлагает, детерминированный код ветирует", тот же, что UserSpecimenService/
/// MedicationEnrichmentProcessor.ResolveCorrectedName.</summary>
public class OcrNameCorrectorTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly OcrNameCorrector _sut;

    public OcrNameCorrectorTests()
    {
        _sut = new OcrNameCorrector(_client, TestPromptProvider.ReturningFallback(), NullLogger<OcrNameCorrector>.Instance);
    }

    private void SetUpModelResponse(params (int Index, string Corrected)[] corrections)
    {
        var array = corrections.Select(c => new Dictionary<string, JsonElement>
        {
            ["index"] = JsonSerializer.SerializeToElement(c.Index),
            ["corrected"] = JsonSerializer.SerializeToElement(c.Corrected),
        }).ToArray();

        var payload = new Dictionary<string, JsonElement>
        {
            ["corrections"] = JsonSerializer.SerializeToElement(array),
        };
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, payload, null));
    }

    [Fact]
    public async Task CorrectAsync_MixedCyrillicLatinHomoglyphs_AppliesModelCorrection()
    {
        // Ровно пример из заметок пользователя: "СYMАТPИПTАН" (кириллица+латиница вперемешку,
        // КАПС) → "Суматриптан" (один алфавит, обычный регистр).
        SetUpModelResponse((0, "Суматриптан"));

        var result = await _sut.CorrectAsync("СYMАТPИПTАН");

        result.Should().Be("Суматриптан");
    }

    [Fact]
    public async Task CorrectAsync_LowSimilarityCorrection_RejectedByTrigramVeto_KeepsOriginal()
    {
        // Модель "исправила" на совсем другое понятие, а не поправила написание — детерминированное
        // вето должно отклонить, как MedicationEnrichmentProcessor.ResolveCorrectedName.
        SetUpModelResponse((0, "Парацетамол"));

        var result = await _sut.CorrectAsync("Ибупрофен");

        result.Should().Be("Ибупрофен");
    }

    [Fact]
    public async Task CorrectAsync_ModelUnavailable_KeepsOriginal_DoesNotThrow()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LmStudioJsonResult.Failure("недоступен"));

        var result = await _sut.CorrectAsync("СYMАТPИПTАН");

        result.Should().Be("СYMАТPИПTАН");
    }

    [Fact]
    public async Task CorrectBatchAsync_PreservesOrderAndDuplicates_OneCallForDistinctNames()
    {
        SetUpModelResponse((0, "Суматриптан"), (1, "Парацетамол"));

        var result = await _sut.CorrectBatchAsync(["СYMАТPИПTАН", "паРАЦЕтамол", "СYMАТPИПTАН"]);

        result.Should().Equal("Суматриптан", "Парацетамол", "Суматриптан");
        await _client.Received(1).ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CorrectAsync_ModelEchoesIndexPrefix_StripsItBeforeApplying()
    {
        // Пересборка enrich-пайплайна: BuildUserText подписывает элементы "[N] Имя" — модель
        // иногда возвращает эту подпись обратно вместе с исправлением; она не должна уехать в
        // DisplayName/AnalyteKey.
        SetUpModelResponse((0, "[0] Суматриптан"));

        var result = await _sut.CorrectAsync("СYMАТPИПTАН");

        result.Should().Be("Суматриптан");
    }

    [Theory]
    // Прод: обе пары давали 0.27/0.12 схожести до кросс-алфавитной свёртки и отклонялись как
    // "другое понятие" — после свёртки (NormalizeAnalyteKey) AnalyteKey у обеих форм и так стал бы
    // одинаковым, но кандидат — ПЕРЕВОД, а не правка написания (MedicalTextTransliterator.
    // IsTranslationOf), поэтому DisplayName/RawDisplayName оставляем как в оригинале — иначе один и
    // тот же бланк при повторном распознавании "мигал" бы языком отображаемого имени. РЕГРЕССИЯ:
    // IsTranslationOf вызывается на СЫРЫХ original/candidate — если бы её вызвали на уже свёрнутых
    // (NormalizeAnalyteKey) строках, HasLatin была бы всегда false и этот тест бы упал (кандидат
    // применился бы вместо того, чтобы остаться оригиналом).
    [InlineData("Антиген Adenovirus (B,C,E)", "Антиген аденовирус (B, C, E)")]
    [InlineData("Антиген Hepatitis B virus surface", "Антиген гепатита В вируса поверхность")]
    public async Task CorrectAsync_ModelTranslatedLatinNameToCyrillic_KeepsOriginal(string original, string translatedCandidate)
    {
        SetUpModelResponse((0, translatedCandidate));

        var result = await _sut.CorrectAsync(original);

        result.Should().Be(original);
    }

    [Fact]
    public void NormalizeAnalyteKey_OfOriginalAndTranslatedCandidate_IsIdentical_NominativeFormPair()
    {
        // Цель миграции AnalyteKey (план "миграция AnalyteKey"): независимо от того, что вернул
        // этот корректор (оригинал остаётся, см. тест выше), итоговый AnalyteKey ОБЕИХ форм — один и
        // тот же, потому что его теперь считает NormalizeAnalyteKey. Раньше (до миграции) это было
        // НЕ так — латинский и кириллический варианты расходились на два разных ключа/тренда.
        // Только для формы без склонения (несклоняемое "аденовирус" на обоих языках) — пара
        // "Hepatitis B virus surface" НЕ достигает точного равенства ключа из-за падежного
        // расхождения словарного перевода и естественной русской фразы, см.
        // LabAnalyteNormalizerTests.NormalizeAnalyteKey_ProductionLogPair_GrammaticalCaseMismatch_KeysStayDifferent.
        LabAnalyteNormalizer.NormalizeAnalyteKey("Антиген Adenovirus (B,C,E)")
            .Should().Be(LabAnalyteNormalizer.NormalizeAnalyteKey("Антиген аденовирус (B, C, E)"));
    }

    [Fact]
    public async Task CorrectBatchAsync_EmptyInput_ReturnsEmpty_DoesNotCallModel()
    {
        var result = await _sut.CorrectBatchAsync([]);

        result.Should().BeEmpty();
        await _client.DidNotReceiveWithAnyArgs().ExtractJsonAsync(default!, default!, Arg.Any<CancellationToken>());
    }
}
