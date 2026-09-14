using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Последний резервный шаг каскада нормы (RefSource.Inferred, см. class doc
/// QualitativeNormJudge) — живой пример, из-за которого появился: бланк без печатного референса,
/// показатель "Микрофлора смешанная... методом световой микроскопии", значение "коккобацилярная,
/// обильно" — ни числовой диапазон, ни полярность "обнаружено/не обнаружено"
/// (IndicatorFlagCalculator.TryApplyInferred) здесь не применимы.</summary>
public class QualitativeNormJudgeTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly QualitativeNormJudge _sut;

    public QualitativeNormJudgeTests()
    {
        _sut = new QualitativeNormJudge(_client, TestPromptProvider.ReturningFallback(), NullLogger<QualitativeNormJudge>.Instance);
    }

    private void SetUpModelResponse(bool? isNormal, double confidence = 0.8) =>
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new { isNormal, confidence })), null));

    [Fact]
    public async Task JudgeAsync_ModelSaysNormal_ReturnsTrue()
    {
        SetUpModelResponse(true);

        var result = await _sut.JudgeAsync(
            "Микрофлора смешанная обнаружение в отделяемом слизистой влагалища методом световой микроскопии",
            "единичная, скудно", unit: null, modelExpectedNorm: null, kbHint: null);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task JudgeAsync_ModelSaysAbnormal_ReturnsFalse()
    {
        SetUpModelResponse(false);

        var result = await _sut.JudgeAsync(
            "Микрофлора смешанная обнаружение в отделяемом слизистой влагалища методом световой микроскопии",
            "коккобацилярная, обильно", unit: null, modelExpectedNorm: null, kbHint: null);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task JudgeAsync_ModelUnsure_ReturnsNull_DoesNotGuess()
    {
        // Модель сама вернула isNormal:null (недостаточно контекста) — не угадываем вместо неё.
        SetUpModelResponse(null);

        var result = await _sut.JudgeAsync("Редкий неоднозначный показатель", "странное значение", null, null, null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task JudgeAsync_ModelUnavailable_ReturnsNull_DoesNotThrow()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LmStudioJsonResult.Failure("недоступен"));

        var result = await _sut.JudgeAsync("Показатель", "значение", null, null, null);

        result.Should().BeNull();
    }
}
