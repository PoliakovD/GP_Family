using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.HealthSummary;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

/// <summary>
/// Сама сборка HealthSummaryService (агрегация 8 существующих сервисов) покрыта интеграционным
/// smoke-тестом (HealthSummaryApiTests) — конструировать здесь вручную ExtractionQueryService/
/// VaccinationService/DoctorReportService со всеми их зависимостями было бы дорогим дублем DI-графа
/// без дополнительной ценности (ни один из них не тестируется так напрямую и в своих файлах — см.
/// отсутствие ExtractionQueryServiceTests). Здесь — только чистая логика выбора «главного показателя»,
/// вынесенная в PickHighlight специально ради юнит-тестируемости без похода в БД.
/// </summary>
public class HealthSummaryServiceTests
{
    private static MyIndicatorSummary Indicator(string key, IndicatorFlag flag, DateOnly date) =>
        new(key, key, Guid.NewGuid(), null, "1", null, flag, date, null, null, "Я");

    [Fact]
    public void PickHighlight_PrefersAbnormalOverNormal_EvenIfOlder()
    {
        var normal = Indicator("glucose", IndicatorFlag.Normal, new DateOnly(2026, 8, 1));
        var abnormal = Indicator("hemoglobin", IndicatorFlag.High, new DateOnly(2026, 1, 1));

        HealthSummaryService.PickHighlight([normal, abnormal]).Should().Be(abnormal);
    }

    [Fact]
    public void PickHighlight_TreatsUnknownFlagAsNormal_NotAbnormal()
    {
        var unknown = Indicator("ferritin", IndicatorFlag.Unknown, new DateOnly(2026, 1, 1));
        var freshNormal = Indicator("glucose", IndicatorFlag.Normal, new DateOnly(2026, 8, 1));

        HealthSummaryService.PickHighlight([unknown, freshNormal]).Should().Be(freshNormal);
    }

    [Fact]
    public void PickHighlight_WithNoAbnormal_PicksMostRecent()
    {
        var older = Indicator("glucose", IndicatorFlag.Normal, new DateOnly(2026, 1, 1));
        var newer = Indicator("hemoglobin", IndicatorFlag.Normal, new DateOnly(2026, 8, 1));

        HealthSummaryService.PickHighlight([older, newer]).Should().Be(newer);
    }

    [Fact]
    public void PickHighlight_FirstAbnormalWins_WhenSeveral()
    {
        var first = Indicator("hemoglobin", IndicatorFlag.Low, new DateOnly(2026, 1, 1));
        var second = Indicator("glucose", IndicatorFlag.High, new DateOnly(2026, 8, 1));

        HealthSummaryService.PickHighlight([first, second]).Should().Be(first);
    }
}
