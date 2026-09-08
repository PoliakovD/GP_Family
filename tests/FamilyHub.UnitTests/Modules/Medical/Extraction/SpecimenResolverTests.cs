using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Резолвинг источника показателя — ResolveAsync (парсинг ответа модели, включая секции с
/// собственной confidence, см. class doc SpecimenSection) и ResolveKbIdAsync (детерминированный
/// гейт поверх ответа: порог confidence + триграммное вето против rawLabel). Пересборка
/// enrich-пайплайна: раньше секции многосекционных бланков ПОДДЕЛЫВАЛИ confidence=1.0 в
/// MedicalDocumentExtractionProcessor, полностью обходя оба этих гейта — здесь проверяется, что
/// сам гейт (одинаковый для документа и для секции) действительно отклоняет низкую уверенность и
/// подмену понятия, если вызывающий передаёт честные значения.
/// </summary>
public class SpecimenResolverTests : SqliteTestBase
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly SpecimenResolver _sut;

    public SpecimenResolverTests()
    {
        // GlobalSpecimenKbService.FindAsync — реальный запрос к БД до триграммного вето (не только
        // после), нужна настоящая (пусть и SQLite) база, не null/мок конкретного класса.
        var specimenKb = new GlobalSpecimenKbService(Db, _client, TestPromptProvider.ReturningFallback(), NullLogger<GlobalSpecimenKbService>.Instance);
        _sut = new SpecimenResolver(_client, specimenKb, TestPromptProvider.ReturningFallback(), NullLogger<SpecimenResolver>.Instance);
    }

    private static DocumentContent TextContent(string text) => DocumentContent.FromText(text);

    private void SetUpModelResponse(object payload) =>
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(
                true, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)), null));

    [Fact]
    public async Task ResolveAsync_ParsesSectionConfidence_NotHardcoded()
    {
        SetUpModelResponse(new
        {
            context = (string?)null, rawLabel = (string?)null, evidence = (string?)null, confidence = 0.0,
            sections = new[]
            {
                new { context = "эякулят", confidence = 0.92, indicatorNames = new[] { "Объём" } },
                new { context = "мутное пятно", confidence = 0.15, indicatorNames = new[] { "Странный показатель" } },
            },
        });

        var result = await _sut.ResolveAsync(TextContent("некий бланк"));

        result.Sections.Should().HaveCount(2);
        result.Sections.Should().Contain(s => s.Context == "эякулят" && Math.Abs(s.Confidence - 0.92) < 0.001);
        result.Sections.Should().Contain(s => s.Context == "мутное пятно" && Math.Abs(s.Confidence - 0.15) < 0.001);
    }

    [Fact]
    public async Task ResolveAsync_SectionWithoutConfidenceField_DefaultsToZero_NotOne()
    {
        // Старый (ещё не обновлённый из админки) текст промпта не отдаёт "confidence" на уровне
        // секции вовсе — дефолт должен быть консервативным (0), не воспроизводить прежний баг (1.0).
        SetUpModelResponse(new
        {
            context = (string?)null, rawLabel = (string?)null, evidence = (string?)null, confidence = 0.0,
            sections = new[] { new { context = "эякулят", indicatorNames = new[] { "Объём" } } },
        });

        var result = await _sut.ResolveAsync(TextContent("некий бланк"));

        result.Sections.Should().ContainSingle();
        result.Sections[0].Confidence.Should().Be(0);
    }

    [Fact]
    public async Task ResolveKbIdAsync_LowConfidence_ReturnsUnresolved_RegardlessOfContext()
    {
        var result = await _sut.ResolveKbIdAsync("эякулят", confidence: 0.2, rawLabel: "эякулят");

        result.Should().Be(SpecimenContextIds.Unresolved,
            "confidence ниже MinConfidence (0.7) должен отклонять контекст, даже правдоподобный");
    }

    [Fact]
    public async Task ResolveKbIdAsync_NullContext_ReturnsUnresolved()
    {
        var result = await _sut.ResolveKbIdAsync(null, confidence: 0.95, rawLabel: "что-то");

        result.Should().Be(SpecimenContextIds.Unresolved);
    }

    [Fact]
    public async Task ResolveKbIdAsync_HighConfidence_ButRawLabelUnrelated_VetoesRegistration()
    {
        // rawLabel документа ("моча") не имеет отношения к предложенному context ("эякулят") —
        // триграммное вето должно отклонить, даже при высокой заявленной confidence.
        var result = await _sut.ResolveKbIdAsync("эякулят", confidence: 0.95, rawLabel: "моча");

        result.Should().Be(SpecimenContextIds.Unresolved,
            "модель не должна была подменить понятие — низкая схожесть context/rawLabel это ловит");
    }
}
