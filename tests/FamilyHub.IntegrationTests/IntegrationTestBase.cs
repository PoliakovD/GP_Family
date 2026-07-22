using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FamilyHub.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public abstract class IntegrationTestBase(FamilyHubWebFactory factory)
{
    /// <summary>
    /// Ответы эндпоинтов — анонимные объекты, сериализуемые ASP.NET Core веб-дефолтами
    /// (camelCase). HttpClient.ReadFromJsonAsync без опций регистронезависимости не матчит
    /// "Id" (наш DTO) с "id" (тело ответа) — поэтому везде используем JsonSerializerDefaults.Web.
    /// </summary>
    protected static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected FamilyHubWebFactory Factory { get; } = factory;

    /// <summary>
    /// HTTP-клиент, аутентифицированный через Dev-схему (X-Dev-TelegramId) как пользователь
    /// с данным Telegram ID — настоящий конвейер аутентификации/авторизации, без подделки initData.
    /// Уникальный telegramId на тест = свой изолированный пользователь, без пересечений между тестами.
    /// Пользователь сразу принимает актуальное согласие ПДн (задача 2.3) — почти всем тестам
    /// нужен «согласившийся»; сценарии без согласия используют Factory.CreateClientAs напрямую.
    /// </summary>
    protected HttpClient ClientAs(long telegramId)
    {
        var client = Factory.CreateClientAs(telegramId);
        AcceptCurrentConsent(client);
        return client;
    }

    /// <summary>Принимает актуальную версию согласия от имени клиента (синхронно — вызов из конструкторов хелперов).</summary>
    protected static void AcceptCurrentConsent(HttpClient client)
    {
        var current = client.GetFromJsonAsync<ConsentVersionDto>("/api/consents/current", JsonOpts)
            .GetAwaiter().GetResult();
        client.PostAsJsonAsync("/api/consents/accept", new { version = current!.Version })
            .GetAwaiter().GetResult().EnsureSuccessStatusCode();
    }

    private sealed record ConsentVersionDto(string Version);

    /// <summary>Неаутентифицированный клиент — без заголовка X-Dev-TelegramId.</summary>
    protected HttpClient AnonymousClient() => Factory.CreateClient();

    /// <summary>Гарантированно уникальный Telegram ID для теста (избегаем коллизий между тестами одной коллекции).</summary>
    protected static long FreshTelegramId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
}
