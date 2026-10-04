using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Сводка для ВРАЧА (план "качество ИИ-распознавания анализов", Этап 4) — отдельный вызов от
/// LabSummarizer (пациентской). Гейт — тот же приём, что LabSummarizer: usedIndicatorNames обязаны
/// ссылаться на реально переданные показатели, иначе вся сводка отклоняется.
/// </summary>
public class ClinicianLabSummarizerTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly ClinicianLabSummarizer _sut;

    public ClinicianLabSummarizerTests()
    {
        _sut = new ClinicianLabSummarizer(_client, TestPromptProvider.ReturningFallback(), NullLogger<ClinicianLabSummarizer>.Instance);
    }

    private void SetUpModelResponse(string? overview, (string Name, string Clinical)[] deviations, string[] usedNames, string? dataQualityNote = null)
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["overview"] = JsonSerializer.SerializeToElement(overview),
            ["deviations"] = JsonSerializer.SerializeToElement(deviations.Select(d => new { name = d.Name, clinical = d.Clinical }).ToArray()),
            ["dataQualityNote"] = JsonSerializer.SerializeToElement(dataQualityNote),
            ["usedIndicatorNames"] = JsonSerializer.SerializeToElement(usedNames),
        };
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new LmStudioJsonResult(true, payload, null));
    }

    private static LabIndicator Indicator(string name, string value, string? unit, IndicatorFlag flag, RefSource refSource = RefSource.Blank) => new()
    {
        DisplayName = name,
        ValueRaw = value,
        Unit = unit,
        Flag = flag,
        RefSource = refSource,
    };

    [Fact]
    public async Task SummarizeAsync_NoIndicators_ReturnsFailureWithoutCallingModel()
    {
        var result = await _sut.SummarizeAsync([], ageYears: null, sex: null);

        result.Success.Should().BeFalse();
        _client.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SummarizeAsync_ModelReturnsValidClinicalSummary_ReturnsOk()
    {
        var indicators = new List<LabIndicator> { Indicator("Глюкоза", "6.8", "ммоль/л", IndicatorFlag.High) };
        SetUpModelResponse(
            "Гипергликемия натощак.",
            [("Глюкоза", "6.8 ммоль/л при референсе 4.11-6.1 — умеренная гипергликемия натощак")],
            ["Глюкоза"]);

        var result = await _sut.SummarizeAsync(indicators, ageYears: 40, sex: Gender.Male);

        result.Success.Should().BeTrue();
        result.Summary!.Overview.Should().Be("Гипергликемия натощак.");
        result.Summary.Deviations.Should().ContainSingle(d => d.Name == "Глюкоза" && d.Clinical.Contains("гипергликемия"));
    }

    [Fact]
    public async Task SummarizeAsync_ModelReferencesUnknownIndicator_RejectsWholeSummary()
    {
        // Тот же антигаллюцинационный гейт, что LabSummarizer — usedIndicatorNames обязаны быть
        // среди РЕАЛЬНО переданных показателей.
        var indicators = new List<LabIndicator> { Indicator("Глюкоза", "5.0", "ммоль/л", IndicatorFlag.Normal) };
        SetUpModelResponse("Всё в норме.", [], ["Показатель, которого не было"]);

        var result = await _sut.SummarizeAsync(indicators, ageYears: null, sex: null);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task SummarizeAsync_DeviationReferencesUnknownIndicator_DroppedButSummaryKept()
    {
        var indicators = new List<LabIndicator> { Indicator("Глюкоза", "6.8", "ммоль/л", IndicatorFlag.High) };
        SetUpModelResponse(
            "Гипергликемия.",
            [("Глюкоза", "выше нормы"), ("Придуманный показатель", "тоже отклонение")],
            ["Глюкоза"]);

        var result = await _sut.SummarizeAsync(indicators, ageYears: null, sex: null);

        result.Success.Should().BeTrue();
        result.Summary!.Deviations.Should().ContainSingle(d => d.Name == "Глюкоза");
    }

    [Fact]
    public async Task SummarizeAsync_ModelUnavailable_ReturnsTransientFailure()
    {
        var indicators = new List<LabIndicator> { Indicator("Глюкоза", "5.0", "ммоль/л", IndicatorFlag.Normal) };
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(LmStudioJsonResult.Failure("Локальный сервер распознавания недоступен.", isTransient: true));

        var result = await _sut.SummarizeAsync(indicators, ageYears: null, sex: null);

        result.Success.Should().BeFalse();
        result.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task SummarizeAsync_PassesAgeAndSexAndInferredNormMarker_InUserText()
    {
        // Врачу важно знать, что норма — оценка ИИ, а не значение с бланка/справочника (см.
        // BuildUserText) — проверяем, что этот контекст реально доходит до модели.
        var indicators = new List<LabIndicator> { Indicator("Тестостерон", "3.2", "нг/мл", IndicatorFlag.Normal, RefSource.Inferred) };
        string? capturedUserText = null;
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Do<string>(t => capturedUserText = t), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new LmStudioJsonResult(true, new Dictionary<string, JsonElement>
            {
                ["overview"] = JsonSerializer.SerializeToElement("В норме."),
                ["deviations"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
                ["usedIndicatorNames"] = JsonSerializer.SerializeToElement(new[] { "Тестостерон" }),
            }, null));

        await _sut.SummarizeAsync(indicators, ageYears: 35, sex: Gender.Male);

        capturedUserText.Should().Contain("35 лет").And.Contain("мужской пол").And.Contain("оценка ИИ");
    }

    [Fact]
    public async Task SummarizeAsync_IndicatorsWithPanels_InputGroupedUnderPanelHeadings()
    {
        // Раздел бланка — только структура входа модели (заголовки), в блоке порядок бланка (Position).
        var indicators = new List<LabIndicator>
        {
            Indicator("Ферритин", "9", "нг/мл", IndicatorFlag.Low),
        };
        indicators[0].Position = 2;
        var neu = Indicator("Нейтрофилы", "80", "%", IndicatorFlag.High);
        neu.Position = 1;
        neu.PanelLabel = "Лейкоцитарная формула";
        var hgb = Indicator("Гемоглобин", "134", "г/л", IndicatorFlag.Normal);
        hgb.Position = 0;
        hgb.PanelLabel = "Общий анализ крови";
        indicators.AddRange([neu, hgb]);
        SetUpModelResponse("Ок.", [], ["Гемоглобин"]);

        await _sut.SummarizeAsync(indicators, ageYears: null, sex: null);

        var userText = (string)_client.ReceivedCalls().Single().GetArguments()[1]!;
        userText.Should().ContainAll("## Общий анализ крови", "## Лейкоцитарная формула", "## Прочие показатели");
        userText.IndexOf("## Общий анализ крови", StringComparison.Ordinal).Should()
            .BeLessThan(userText.IndexOf("- Гемоглобин", StringComparison.Ordinal));
        userText.IndexOf("- Нейтрофилы", StringComparison.Ordinal).Should()
            .BeLessThan(userText.IndexOf("## Прочие показатели", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SummarizeAsync_IndicatorsWithoutPanels_InputHasNoHeadings()
    {
        SetUpModelResponse("Ок.", [], ["Глюкоза"]);

        await _sut.SummarizeAsync([Indicator("Глюкоза", "5", "ммоль/л", IndicatorFlag.Normal)], ageYears: null, sex: null);

        var userText = (string)_client.ReceivedCalls().Single().GetArguments()[1]!;
        userText.Should().NotContain("##");
    }
}
