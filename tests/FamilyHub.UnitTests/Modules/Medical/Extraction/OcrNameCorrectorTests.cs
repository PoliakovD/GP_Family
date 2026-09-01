using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
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
        _sut = new OcrNameCorrector(_client, NullLogger<OcrNameCorrector>.Instance);
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
    public async Task CorrectBatchAsync_EmptyInput_ReturnsEmpty_DoesNotCallModel()
    {
        var result = await _sut.CorrectBatchAsync([]);

        result.Should().BeEmpty();
        await _client.DidNotReceiveWithAnyArgs().ExtractJsonAsync(default!, default!, Arg.Any<CancellationToken>());
    }
}
