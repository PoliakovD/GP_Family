using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.LmStudio;

/// <summary>
/// Фолбэк на невалидный JSON (см. class doc LmStudioJsonClient.JsonRepairSystemPrompt) —
/// несмотря на промпт задачи, модель иногда отдаёт почти-валидный JSON с мелким синтаксическим
/// дефектом; второй, узко-специализированный проход "почини синтаксис" должен восстанавливать
/// такие случаи вместо того, чтобы сразу проваливать вызов. Фейковым HttpMessageHandler, без
/// реального обращения к LM Studio — тот же приём, что BraveSearchProviderTests/YandexSearchProviderTests.
/// </summary>
public class LmStudioJsonClientTests
{
    private sealed class RecordingHttpMessageHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];
        private int _callCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _callCount++;
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(_callCount, request);
        }
    }

    private static LmStudioJsonClient CreateSut(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var options = Options.Create(new LmStudioOptions());
        var modelProvider = Substitute.For<ILmStudioModelProvider>();
        modelProvider.GetActiveModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult((string)ci[0]));
        return new LmStudioJsonClient(httpClient, options, new LmStudioConcurrencyGate(), modelProvider, NullLogger<LmStudioJsonClient>.Instance);
    }

    private static HttpResponseMessage ChatResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { choices = new[] { new { message = new { content } } } }),
    };

    /// <summary>Читает поля запроса через JsonDocument, а не строковым Contains по сырому телу —
    /// System.Text.Json по умолчанию экранирует кириллицу как \uXXXX на записи, сырой Contains по
    /// незаэкранированному тексту был бы ломким независимо от содержимого промптов.</summary>
    private static (string SystemPrompt, string UserText) ParseRequest(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        var messages = doc.RootElement.GetProperty("messages");
        var systemPrompt = messages[0].GetProperty("content").GetString()!;
        var userText = messages[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        return (systemPrompt, userText);
    }

    [Fact]
    public async Task ExtractJsonAsync_ValidJsonOnFirstTry_ReturnsSuccess_MakesOnlyOneCall()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => ChatResponse("""{"name": "Парацетамол"}"""));
        var sut = CreateSut(handler);

        var result = await sut.ExtractJsonAsync("система", "пользователь");

        result.Success.Should().BeTrue();
        result.Payload!["name"].GetString().Should().Be("Парацетамол");
        handler.RequestBodies.Should().HaveCount(1, "валидный JSON с первого раза не должен вызывать починку");
    }

    [Fact]
    public async Task ExtractJsonAsync_InvalidJsonOnFirstTry_RepairSucceeds_ReturnsRepairedPayload()
    {
        // Пропущенная запятая между полями — типичный мелкий дефект.
        const string broken = """{"name": "Парацетамол" "form": "таблетки"}""";
        const string repaired = """{"name": "Парацетамол", "form": "таблетки"}""";

        var handler = new RecordingHttpMessageHandler((call, _) => ChatResponse(call == 1 ? broken : repaired));
        var sut = CreateSut(handler);

        var result = await sut.ExtractJsonAsync("система", "пользователь");

        result.Success.Should().BeTrue();
        result.Payload!["name"].GetString().Should().Be("Парацетамол");
        result.Payload!["form"].GetString().Should().Be("таблетки");
        handler.RequestBodies.Should().HaveCount(2, "первый проход не распарсился — должна была случиться попытка починки");
        var (repairSystemPrompt, repairUserText) = ParseRequest(handler.RequestBodies[1]);
        repairSystemPrompt.Should().Contain("исправлению синтаксиса JSON", "второй вызов — починка, не повтор исходной задачи");
        repairUserText.Should().Be(broken, "на починку должен уйти именно сломанный кандидат, не исходный системный промпт задачи");
    }

    [Fact]
    public async Task ExtractJsonAsync_InvalidJsonOnBothTries_ReturnsFailure()
    {
        const string stillBroken = """{"name": "Парацетамол" "form": "таблетки"}""";
        var handler = new RecordingHttpMessageHandler((_, _) => ChatResponse(stillBroken));
        var sut = CreateSut(handler);

        var result = await sut.ExtractJsonAsync("система", "пользователь");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        handler.RequestBodies.Should().HaveCount(2, "должна была случиться ровно одна попытка починки, не бесконечный цикл");
    }

    [Fact]
    public async Task ExtractJsonAsync_RepairCallReturnsEmpty_ReturnsFailure_DoesNotThrow()
    {
        const string broken = """{"name": "Парацетамол" "form": "таблетки"}""";
        var handler = new RecordingHttpMessageHandler((call, _) => ChatResponse(call == 1 ? broken : string.Empty));
        var sut = CreateSut(handler);

        var result = await sut.ExtractJsonAsync("система", "пользователь");

        result.Success.Should().BeFalse();
        handler.RequestBodies.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractJsonAsync_RepairCallHttpError_ReturnsFailure_DoesNotThrow()
    {
        const string broken = """{"name": "Парацетамол" "form": "таблетки"}""";
        var handler = new RecordingHttpMessageHandler((call, _) =>
            call == 1 ? ChatResponse(broken) : new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("боком", Encoding.UTF8, "text/plain"),
            });
        var sut = CreateSut(handler);

        var result = await sut.ExtractJsonAsync("система", "пользователь");

        result.Success.Should().BeFalse();
        handler.RequestBodies.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractJsonAsync_ValidJsonWithThinkBlockAndCodeFence_ParsesWithoutRepair()
    {
        const string content = """
            <think>рассуждаю...</think>
            ```json
            {"name": "Парацетамол"}
            ```
            """;
        var handler = new RecordingHttpMessageHandler((_, _) => ChatResponse(content));
        var sut = CreateSut(handler);

        var result = await sut.ExtractJsonAsync("система", "пользователь");

        result.Success.Should().BeTrue();
        result.Payload!["name"].GetString().Should().Be("Парацетамол");
        handler.RequestBodies.Should().HaveCount(1);
    }
}
