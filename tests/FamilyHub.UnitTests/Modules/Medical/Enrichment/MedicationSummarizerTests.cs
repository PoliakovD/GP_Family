using System.Text.Json;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Enrichment;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Enrichment;

/// <summary>
/// Антигаллюцинационный гейт (этап 4, task-5.1-medications.md «не генерация из головы»):
/// запись в общий справочник допускается только если модель реально сослалась на переданный
/// сниппет доверенного источника, и в ответе есть хоть какое-то содержательное поле.
/// </summary>
public class MedicationSummarizerTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly MedicationSummarizer _sut;

    public MedicationSummarizerTests()
    {
        _sut = new MedicationSummarizer(_client, NullLogger<MedicationSummarizer>.Instance);
    }

    private static readonly IReadOnlyList<WebSnippet> OneTrustedSnippet =
    [
        new WebSnippet("Видаль", "https://www.vidal.ru/drugs/test", "Тестовое описание препарата."),
    ];

    private static Dictionary<string, JsonElement> Payload(object obj) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(obj))!;

    [Fact]
    public async Task NoSnippets_RejectsWithoutCallingModel()
    {
        var result = await _sut.SummarizeAsync("Тест", []);

        result.Success.Should().BeFalse();
        result.Summary.Should().BeNull();
        _client.ReceivedCalls().Should().BeEmpty("без сниппетов вызывать LLM незачем — экономим локальный инференс");
    }

    [Fact]
    public async Task EmptyUsedSourceIndexes_Rejects_EvenIfFieldsArePopulated()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, Payload(new
            {
                internationalName = "Ибупрофен",
                tradeNames = new[] { "Нурофен" },
                form = "таблетки",
                purpose = "жаропонижающее",
                storage = (string?)null,
                driving = (string?)null,
                specialNotes = (string?)null,
                usedSourceIndexes = Array.Empty<int>(),
            }), null));

        var result = await _sut.SummarizeAsync("Ибупрофен", OneTrustedSnippet);

        result.Success.Should().BeFalse("модель не сослалась ни на один источник — доверять содержимому нельзя");
    }

    [Fact]
    public async Task AllFieldsEmpty_Rejects_EvenWithSourceIndex()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, Payload(new
            {
                internationalName = (string?)null,
                tradeNames = Array.Empty<string>(),
                form = (string?)null,
                purpose = (string?)null,
                storage = (string?)null,
                driving = (string?)null,
                specialNotes = (string?)null,
                usedSourceIndexes = new[] { 0 },
            }), null));

        var result = await _sut.SummarizeAsync("Ибупрофен", OneTrustedSnippet);

        result.Success.Should().BeFalse("сослаться на источник и не извлечь из него ничего — тоже недостаточно для записи в справочник");
    }

    [Fact]
    public async Task ModelCallFails_PropagatesError()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LmStudioJsonResult.Failure("Локальный сервер распознавания недоступен."));

        var result = await _sut.SummarizeAsync("Ибупрофен", OneTrustedSnippet);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Локальный сервер распознавания недоступен.");
    }

    [Fact]
    public async Task ValidResponse_ReturnsSummaryWithMappedFields()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, Payload(new
            {
                internationalName = "Ибупрофен",
                tradeNames = new[] { "Нурофен", "Ибуфен" },
                form = "таблетки",
                purpose = "жаропонижающее и обезболивающее",
                storage = "в сухом месте при температуре не выше 25°C",
                driving = "не влияет",
                specialNotes = (string?)null,
                usedSourceIndexes = new[] { 0, 0, 99 }, // дубли и индекс за пределами массива — должны отфильтроваться
            }), null));

        var result = await _sut.SummarizeAsync("Нурофен", OneTrustedSnippet);

        result.Success.Should().BeTrue();
        result.Summary!.InternationalName.Should().Be("Ибупрофен");
        result.Summary.TradeNames.Should().BeEquivalentTo("Нурофен", "Ибуфен");
        result.Summary.Form.Should().Be("таблетки");
        result.Summary.UsedSourceIndexes.Should().BeEquivalentTo(new[] { 0 }, "дубли и индексы вне диапазона переданных сниппетов отфильтрованы");
    }

    [Fact]
    public async Task ValidResponse_MapsUsageField()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, Payload(new
            {
                internationalName = "Ибупрофен",
                tradeNames = Array.Empty<string>(),
                form = (string?)null,
                purpose = (string?)null,
                usage = "Внутрь, после еды, по 1 таблетке (200 мг) до 3-4 раз в сутки — как указано в инструкции.",
                storage = (string?)null,
                driving = (string?)null,
                specialNotes = (string?)null,
                usedSourceIndexes = new[] { 0 },
            }), null));

        var result = await _sut.SummarizeAsync("Ибупрофен", OneTrustedSnippet);

        // Этап 4 «не ограничивай модель»: способ применения/дозы из официальной инструкции —
        // обычное поле справочника, не отфильтровывается как медконсультация; единственное поле
        // с содержанием всё равно должно проходить гейт "hasContent".
        result.Success.Should().BeTrue();
        result.Summary!.Usage.Should().Contain("200 мг");
    }
}
