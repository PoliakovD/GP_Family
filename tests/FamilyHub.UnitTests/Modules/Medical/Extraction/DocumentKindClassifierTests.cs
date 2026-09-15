using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Определение вида документа для батч-загрузки (см. class doc DocumentKindClassifier) —
/// парсинг ответа модели (analysis/visit/мусор) и проброс технического сбоя как
/// IsTransientFailure=true (тот же контракт, что LegitimacyGuardService/SpecimenResolver, чтобы
/// вызывающая сторона могла отличить "модель отклонила" от "модель недоступна").
/// </summary>
public class DocumentKindClassifierTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly DocumentKindClassifier _sut;

    public DocumentKindClassifierTests()
    {
        _sut = new DocumentKindClassifier(_client, TestPromptProvider.ReturningFallback(), NullLogger<DocumentKindClassifier>.Instance);
    }

    private static DocumentContent TextContent(string text) => DocumentContent.FromText(text);

    private void SetUpModelResponse(object payload) =>
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(
                true, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)), null));

    [Fact]
    public async Task ClassifyAsync_ModelSaysAnalysis_ReturnsAnalysis()
    {
        SetUpModelResponse(new { kind = "analysis", confidence = 0.9, reason = "таблица показателей" });

        var result = await _sut.ClassifyAsync(TextContent("Гемоглобин 140 г/л, норма 130-160"));

        result.Kind.Should().Be(MedicalRecordKind.Analysis);
        result.IsTransientFailure.Should().BeFalse();
    }

    [Fact]
    public async Task ClassifyAsync_ModelSaysVisit_ReturnsDoctorVisit()
    {
        SetUpModelResponse(new { kind = "visit", confidence = 0.85, reason = "диагноз и рекомендации" });

        var result = await _sut.ClassifyAsync(TextContent("Диагноз: ОРВИ. Рекомендации: покой, обильное питьё."));

        result.Kind.Should().Be(MedicalRecordKind.DoctorVisit);
    }

    [Fact]
    public async Task ClassifyAsync_ModelReturnsGarbageKind_ReturnsNullKind_NotThrows()
    {
        SetUpModelResponse(new { kind = "unknown_thing", confidence = 0.1, reason = "не уверен" });

        var result = await _sut.ClassifyAsync(TextContent("что-то невнятное"));

        result.Kind.Should().BeNull("мусорный ответ модели не должен молча стать конкретным видом — вызывающая сторона сама решает дефолт");
    }

    [Fact]
    public async Task ClassifyAsync_TechnicalFailure_PropagatesIsTransientFailure()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LmStudioJsonResult.Failure("Сервер недоступен", isTransient: true));

        var result = await _sut.ClassifyAsync(TextContent("любой текст"));

        result.Kind.Should().BeNull();
        result.IsTransientFailure.Should().BeTrue(
            "технический сбой должен пробрасываться как исключение выше по стеку (LmStudioMedicalDocumentExtractor), не молча стать дефолтом");
    }

    [Fact]
    public async Task ClassifyAsync_NoTextNoImages_ReturnsNullKind_WithoutCallingModel()
    {
        var emptyContent = new DocumentContent(DocumentSourceKind.Text, null, []);

        var result = await _sut.ClassifyAsync(emptyContent);

        result.Kind.Should().BeNull();
        _client.ReceivedCalls().Should().BeEmpty();
    }
}
