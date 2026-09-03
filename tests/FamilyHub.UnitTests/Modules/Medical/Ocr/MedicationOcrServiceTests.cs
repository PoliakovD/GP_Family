using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Ocr;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Ocr;

public class MedicationOcrServiceTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly MedicationOcrService _sut;

    public MedicationOcrServiceTests()
    {
        // Второй проход коррекции OCR (OcrNameCorrector) вызывает тот же клиент отдельным
        // (текстовым, без фото) запросом — по умолчанию не застаблен ⇒ NSubstitute вернёт
        // Task<LmStudioJsonResult>(null); OcrNameCorrector трактует null/Success=false как
        // "коррекция недоступна" и просто оставляет исходное имя, тесты ниже это не ловят
        // отдельно, а полагаются на конкретный стаб в успешном сценарии.
        var prompts = TestPromptProvider.ReturningFallback();
        var nameCorrector = new OcrNameCorrector(_client, prompts, NullLogger<OcrNameCorrector>.Instance);
        _sut = new MedicationOcrService(_client, nameCorrector, prompts, NullLogger<MedicationOcrService>.Instance);
    }

    /// <summary>Length берётся из объявленного конструктору значения, а не из реального размера
    /// потока — проверка в ExtractAsync срабатывает ДО чтения потока, поэтому для теста
    /// "слишком большого" файла не нужно реально аллоцировать мегабайты.</summary>
    private static IFormFile FakeFile(long length, string contentType = "image/jpeg", byte[]? bytes = null)
    {
        var stream = new MemoryStream(bytes ?? []);
        return new FormFile(stream, 0, length, "file", "photo.jpg")
        {
            Headers = new HeaderDictionary { ["Content-Type"] = contentType },
        };
    }

    private static IFormFileCollection FilesOf(params IFormFile[] files)
    {
        var collection = new FormFileCollection();
        collection.AddRange(files);
        return collection;
    }

    // Регрессия на аудит module-review-2026-08-02/04, находка 2: раньше размер файла не
    // ограничивался явно — только implicit-дефолтом Kestrel на весь запрос.
    [Fact]
    public async Task ExtractAsync_PhotoOverSizeLimit_ReturnsFailure_WithoutCallingLmStudio()
    {
        var oversized = FakeFile(length: 2 * 1024 * 1024);

        var result = await _sut.ExtractAsync(FilesOf(oversized));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("1 МБ");
        await _client.DidNotReceiveWithAnyArgs().ExtractJsonAsync(
            default!, default!, Arg.Any<IReadOnlyList<(byte[], string)>>());
    }

    [Fact]
    public async Task ExtractAsync_PhotoAtOrUnderSizeLimit_ProceedsToLmStudio()
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement("Тестпрепарат"),
        };
        _client.ExtractJsonAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(byte[], string)>>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, payload, null));

        var withinLimit = FakeFile(length: 1 * 1024 * 1024, bytes: [1, 2, 3]);

        var result = await _sut.ExtractAsync(FilesOf(withinLimit));

        result.Success.Should().BeTrue();
        result.Name.Should().Be("Тестпрепарат");
    }

    [Fact]
    public async Task ExtractAsync_TooManyPhotos_ReturnsFailure()
    {
        var files = FilesOf(FakeFile(100), FakeFile(100), FakeFile(100), FakeFile(100), FakeFile(100), FakeFile(100));

        var result = await _sut.ExtractAsync(files);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("5 фото");
    }

    [Fact]
    public async Task ExtractAsync_NonImageContentType_ReturnsFailure()
    {
        var result = await _sut.ExtractAsync(FilesOf(FakeFile(100, contentType: "application/pdf")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("изображения");
    }
}
