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
    /// </summary>
    protected HttpClient ClientAs(long telegramId) => Factory.CreateClientAs(telegramId);

    /// <summary>Неаутентифицированный клиент — без заголовка X-Dev-TelegramId.</summary>
    protected HttpClient AnonymousClient() => Factory.CreateClient();

    /// <summary>Гарантированно уникальный Telegram ID для теста (избегаем коллизий между тестами одной коллекции).</summary>
    protected static long FreshTelegramId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
}
