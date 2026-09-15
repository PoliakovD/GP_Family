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
/// QualitativeNormJudge) — два живых примера: (1) бланк без печатного референса, показатель
/// "Микрофлора смешанная... методом световой микроскопии", значение "коккобацилярная, обильно" —
/// ни числовой диапазон, ни полярность "обнаружено/не обнаружено" здесь не применимы; (2) значение
/// качественное ("не обнаружено"), а известный диапазон начинается не с нуля (например "2-10") —
/// механическое "ниже диапазона ⇒ Low" здесь ошибочно.</summary>
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
            "единичная, скудно", unit: null, modelExpectedNorm: null,
            refLow: null, refHigh: null, kbNorm: null);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task JudgeAsync_ModelSaysAbnormal_ReturnsFalse()
    {
        SetUpModelResponse(false);

        var result = await _sut.JudgeAsync(
            "Микрофлора смешанная обнаружение в отделяемом слизистой влагалища методом световой микроскопии",
            "коккобацилярная, обильно", unit: null, modelExpectedNorm: null,
            refLow: null, refHigh: null, kbNorm: null);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task JudgeAsync_ModelUnsure_ReturnsNull_DoesNotGuess()
    {
        // Модель сама вернула isNormal:null (недостаточно контекста) — не угадываем вместо неё.
        SetUpModelResponse(null);

        var result = await _sut.JudgeAsync(
            "Редкий неоднозначный показатель", "странное значение", null, null, null, null, null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task JudgeAsync_ModelUnavailable_ReturnsNull_DoesNotThrow()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LmStudioJsonResult.Failure("недоступен"));

        var result = await _sut.JudgeAsync("Показатель", "значение", null, null, null, null, null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task JudgeAsync_AbsenceBelowNonZeroRange_PassesBoundsAndLowMeansToModel_ModelCanStillCallItNormal()
    {
        // Живой случай: диапазон KB "2-10" (не с нуля), значение — качественное отсутствие. Сам
        // JudgeAsync не решает этот вопрос сам — просто обязан довезти контекст до модели, чтобы
        // ОНА (не наш код) могла решить, что отсутствие тут норма (LowMeans).
        SetUpModelResponse(true);
        var kbNorm = new LabAnalyteKbPayload.KbNormExplanations(
            PlainExplanation: "Показатель микрофлоры.",
            HighMeans: "Повышение указывает на дисбиоз.",
            LowMeans: "Отсутствие или следовые количества — вариант нормы для здоровой микрофлоры.");

        var result = await _sut.JudgeAsync(
            "Лактобациллы", "не обнаружено", unit: null, modelExpectedNorm: null,
            refLow: 2, refHigh: 10, kbNorm: kbNorm);

        result.Should().BeTrue();

        var userText = (string)_client.ReceivedCalls().Single().GetArguments()[1]!;
        userText.Should().Contain("2").And.Contain("10", "границы диапазона должны дойти до модели, даже если сам результат — качественный текст");
        userText.Should().Contain("Отсутствие или следовые количества — вариант нормы для здоровой микрофлоры.",
            "LowMeans должен быть виден модели отдельно от HighMeans, а не потерян в общей склейке");
        userText.Should().Contain("Повышение указывает на дисбиоз.");
    }
}
