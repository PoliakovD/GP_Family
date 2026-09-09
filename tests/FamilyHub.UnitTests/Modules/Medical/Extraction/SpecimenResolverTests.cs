using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.TestUtils;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Резолвинг источника показателя — ResolveAsync (парсинг ответа модели, включая NeedsSite —
/// обобщённое слово без локализации, см. class doc SpecimenDocumentResolution) и ResolveKbIdAsync
/// (детерминированный гейт поверх ответа: порог confidence + триграммное вето против rawLabel).
/// </summary>
public class SpecimenResolverTests : SqliteTestBase
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly SpecimenResolver _sut;

    public SpecimenResolverTests()
    {
        // GlobalSpecimenKbService.FindAsync — реальный запрос к БД до триграммного вето (не только
        // после), нужна настоящая (пусть и SQLite) база, не null/мок конкретного класса.
        var specimenKb = new GlobalSpecimenKbService(
            Db, _client, TestPromptProvider.ReturningFallback(), new AdminCatalogService(Db), NullLogger<GlobalSpecimenKbService>.Instance);
        _sut = new SpecimenResolver(_client, specimenKb, TestPromptProvider.ReturningFallback(), NullLogger<SpecimenResolver>.Instance);
    }

    private static DocumentContent TextContent(string text) => DocumentContent.FromText(text);

    private void SetUpModelResponse(object payload) =>
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(
                true, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)), null));

    [Fact]
    public async Task ResolveAsync_GenericSwabWithoutSite_ReturnsNullContextAndNeedsSiteHint()
    {
        // Модель видит "мазок" без уточнения места — не регистрирует голый термин как context,
        // возвращает его отдельно в needsSite (заметка 2 — UI должен спросить пользователя, откуда).
        SetUpModelResponse(new
        {
            context = (string?)null, rawLabel = "мазок", evidence = "мазок", confidence = 0.0,
            needsSite = "мазок",
        });

        var result = await _sut.ResolveAsync(TextContent("некий бланк"));

        result.Context.Should().BeNull();
        result.NeedsSite.Should().Be("мазок");
    }

    [Fact]
    public async Task ResolveAsync_SwabWithSite_ReturnsLocalizedContext_NeedsSiteNull()
    {
        SetUpModelResponse(new
        {
            context = "вагинальный мазок", rawLabel = "вагинальный мазок", evidence = "вагинальный мазок",
            confidence = 0.9, needsSite = (string?)null,
        });

        var result = await _sut.ResolveAsync(TextContent("некий бланк"));

        result.Context.Should().Be("вагинальный мазок");
        result.NeedsSite.Should().BeNull();
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
