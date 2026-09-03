using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Pipeline;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>UX-редизайн: гейт "пустая ячейка бланка не должна стать показателем" — модель иногда
/// подставляет плейсхолдер ("нет данных" и т.п.) вместо честного пропуска строки без значения
/// (см. правило в AnalysisSystemPrompt + EmptyValuePlaceholders). Голый прочерк "-"/"—" тоже
/// считается пустой ячейкой и отбрасывается (график по нему не построить); словесные
/// "отсутствуют"/"не обнаружено"/"отрицательно" — настоящие качественные результаты, сохраняются.</summary>
public class LmStudioMedicalDocumentExtractorTests
{
    private readonly IDocumentTextExtractor _textExtractor = Substitute.For<IDocumentTextExtractor>();
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly LmStudioMedicalDocumentExtractor _sut;

    public LmStudioMedicalDocumentExtractorTests()
    {
        // SpecimenResolver.ResolveAsync (единственный метод, который зовёт экстрактор) не
        // обращается к GlobalSpecimenKbService — ей нужна БД, которой в этих тестах нет; null
        // безопасен. Тот же _client — SetUpModelResponse ниже не задаёт context/confidence,
        // поэтому резолвер молча получает пустой SpecimenDocumentResolution, не влияющий на
        // проверяемые в этом файле поля (показатели/врач/заключение).
        var specimenResolver = new SpecimenResolver(
            _client, null!, TestPromptProvider.ReturningFallback(), NullLogger<SpecimenResolver>.Instance);
        _sut = new LmStudioMedicalDocumentExtractor(
            _textExtractor, _client, specimenResolver, TestPromptProvider.ReturningFallback(),
            TestPipelineConfigService.ReturningEnabled(), Options.Create(new ExtractionOptions()),
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

    [Theory]
    [InlineData("нет данных")]
    [InlineData("-")]
    [InlineData("—")]
    public async Task ExtractAsync_PlaceholderOrDashValue_IsDropped(string value)
    {
        SetUpTextChunk($"Лейкоциты {value}");
        SetUpModelResponse(("Лейкоциты", value));

        var result = await _sut.ExtractAsync(new DocumentSource([1], "text/plain", "a.txt"), MedicalRecordKind.Analysis);

        result.LabIndicators.Should().BeEmpty();
    }

    [Theory]
    [InlineData("отсутствуют")]
    [InlineData("не обнаружено")]
    [InlineData("отрицательно")]
    public async Task ExtractAsync_QualitativeNegativeResult_IsKept(string value)
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

        result.LabIndicators!.Select(i => i.Name).Should().BeEquivalentTo(["Гемоглобин", "Эритроциты"]);
    }

    [Fact]
    public async Task ExtractAsync_Analysis_CapturesDoctorFromDocument()
    {
        SetUpTextChunk("Гемоглобин 118 г/л\nВрач: Петрова И.И.");
        var payload = new Dictionary<string, JsonElement>
        {
            ["indicators"] = JsonSerializer.SerializeToElement(new[] { new { name = "Гемоглобин", value = "118" } }),
            ["doctor"] = JsonSerializer.SerializeToElement("Петрова И.И."),
        };
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, payload, null));

        var result = await _sut.ExtractAsync(new DocumentSource([1], "text/plain", "a.txt"), MedicalRecordKind.Analysis);

        result.Doctor.Should().Be("Петрова И.И.");
    }

    [Fact]
    public async Task ExtractAsync_Visit_CapturesDoctorAndStructuredPrescriptions()
    {
        SetUpTextChunk("Диагноз: ОРВИ. Врач: Иванов А.А. Назначено: Парацетамол по 1 таблетке 3 раза в день.");
        var payload = new Dictionary<string, JsonElement>
        {
            ["diagnosis"] = JsonSerializer.SerializeToElement("ОРВИ"),
            ["doctor"] = JsonSerializer.SerializeToElement("Иванов А.А."),
            ["prescriptions"] = JsonSerializer.SerializeToElement(new[]
            {
                new { name = "Парацетамол", dosageInstructions = "по 1 таблетке 3 раза в день" },
            }),
        };
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, payload, null));

        var result = await _sut.ExtractAsync(new DocumentSource([1], "text/plain", "a.txt"), MedicalRecordKind.DoctorVisit);

        result.Doctor.Should().Be("Иванов А.А.");
        result.Conclusion!.Diagnosis.Should().Be("ОРВИ");
        result.Conclusion.PrescribedMedications.Should().ContainSingle(
            m => m.Name == "Парацетамол" && m.DosageInstructions == "по 1 таблетке 3 раза в день");
    }
}
