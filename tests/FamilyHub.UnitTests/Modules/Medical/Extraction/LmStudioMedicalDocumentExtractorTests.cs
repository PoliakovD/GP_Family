using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>UX-редизайн: гейт "пустая ячейка бланка не должна стать показателем" — модель иногда
/// подставляет плейсхолдер ("нет данных" и т.п.) вместо честного пропуска строки без значения
/// (см. правило в AnalysisSystemPrompt + EmptyValuePlaceholders). Прочерк/"отсутствуют" — это
/// РЕАЛЬНОЕ значение бланка, должны сохраняться.</summary>
public class LmStudioMedicalDocumentExtractorTests
{
    private readonly IDocumentTextExtractor _textExtractor = Substitute.For<IDocumentTextExtractor>();
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly LmStudioMedicalDocumentExtractor _sut;

    public LmStudioMedicalDocumentExtractorTests()
    {
        _sut = new LmStudioMedicalDocumentExtractor(
            _textExtractor, _client, Options.Create(new ExtractionOptions()),
            NullLogger<LmStudioMedicalDocumentExtractor>.Instance);
    }

    private void SetUpTextChunk(string text) =>
        _textExtractor.ExtractAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(DocumentContent.FromText(text));

    private void SetUpModelResponse(params (string Name, string Value)[] indicators)
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["indicators"] = JsonSerializer.SerializeToElement(
                indicators.Select(i => new { name = i.Name, value = i.Value }).ToArray()),
        };
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, payload, null));
    }

    [Fact]
    public async Task ExtractAsync_PlaceholderValue_IsDropped()
    {
        SetUpTextChunk("Лейкоциты нет данных");
        SetUpModelResponse(("Лейкоциты", "нет данных"));

        var result = await _sut.ExtractAsync(new DocumentSource([1], "text/plain", "a.txt"), MedicalRecordKind.Analysis);

        result.LabIndicators.Should().BeEmpty();
    }

    [Theory]
    [InlineData("-")]
    [InlineData("—")]
    [InlineData("отсутствуют")]
    [InlineData("не обнаружено")]
    [InlineData("отрицательно")]
    public async Task ExtractAsync_ExplicitDashOrAbsentValue_IsKept(string value)
    {
        SetUpTextChunk($"Глюкоза {value}");
        SetUpModelResponse(("Глюкоза", value));

        var result = await _sut.ExtractAsync(new DocumentSource([1], "text/plain", "a.txt"), MedicalRecordKind.Analysis);

        result.LabIndicators.Should().ContainSingle(i => i.Name == "Глюкоза" && i.Value == value);
    }

    [Fact]
    public async Task ExtractAsync_MixOfRealAndPlaceholderValues_KeepsOnlyReal()
    {
        SetUpTextChunk("Гемоглобин 118 г/л\nТромбоциты -\nЛейкоциты нет данных\nЭритроциты отсутствуют");
        SetUpModelResponse(
            ("Гемоглобин", "118"),
            ("Тромбоциты", "-"),
            ("Лейкоциты", "нет данных"),
            ("Эритроциты", "отсутствуют"));

        var result = await _sut.ExtractAsync(new DocumentSource([1], "text/plain", "a.txt"), MedicalRecordKind.Analysis);

        result.LabIndicators!.Select(i => i.Name).Should().BeEquivalentTo(["Гемоглобин", "Тромбоциты", "Эритроциты"]);
    }
}
