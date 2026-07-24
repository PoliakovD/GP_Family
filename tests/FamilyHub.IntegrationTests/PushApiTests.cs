using System.Net;
using System.Net.Http.Json;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Редизайн навигации, ADR-0004: /api/push/* — подписка/отписка от Web Push, доступ строго по
/// текущему пользователю (как и в /api/notifications). FamilyHubWebFactory не настраивает
/// WebPush:* (нет VAPID) — это же реалистичный dev-дефолт, проверяем и его.
/// </summary>
public class PushApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static object SubscribeBody(string endpoint) => new
    {
        endpoint,
        p256dh = "test-p256dh-key",
        auth = "test-auth-secret",
    };

    [Fact]
    public async Task VapidPublicKey_NotConfiguredInTestEnvironment_Returns404()
    {
        var user = ClientAs(FreshTelegramId());

        var response = await user.GetAsync("/api/push/vapid-public-key");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "тестовое окружение не задаёт WebPush:VapidPublicKey — фронт должен скрывать тумблер");
    }

    [Fact]
    public async Task Subscribe_ThenUnsubscribe_Succeeds()
    {
        var user = ClientAs(FreshTelegramId());
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";

        var subscribeResponse = await user.PostAsJsonAsync("/api/push/subscribe", SubscribeBody(endpoint));
        subscribeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Endpoint зашифрован at-rest (ADR-0002) — фильтровать в SQL по нему нельзя, сравниваем
        // после расшифровки EF на стороне приложения. Таблица общая на весь тестовый коллекшн
        // (контейнер Postgres живёт дольше одного теста) — искать СВОЙ endpoint, а не весь набор строк.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var all = await db.PushSubscriptions.AsNoTracking().ToListAsync();
            all.Should().ContainSingle(s => s.Endpoint == endpoint);
        }

        var unsubscribeResponse = await user.PostAsJsonAsync("/api/push/unsubscribe", new { endpoint });
        unsubscribeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var all = await db.PushSubscriptions.AsNoTracking().ToListAsync();
            all.Should().NotContain(s => s.Endpoint == endpoint);
        }
    }

    [Fact]
    public async Task Unsubscribe_UnknownEndpoint_Returns404()
    {
        var user = ClientAs(FreshTelegramId());

        var response = await user.PostAsJsonAsync("/api/push/unsubscribe", new { endpoint = "https://fcm.googleapis.com/fcm/send/never-subscribed" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unsubscribe_AnotherUsersSubscription_Returns404_AndSubscriptionSurvives()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        await owner.PostAsJsonAsync("/api/push/subscribe", SubscribeBody(endpoint));

        var response = await stranger.PostAsJsonAsync("/api/push/unsubscribe", new { endpoint });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "чужую подписку удалить нельзя, даже зная endpoint");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var all = await db.PushSubscriptions.AsNoTracking().ToListAsync();
        all.Should().Contain(s => s.Endpoint == endpoint, "подписка владельца не должна была удалиться");
    }

    [Fact]
    public async Task Subscribe_SameEndpointAgain_UpsertsInsteadOfDuplicating()
    {
        var user = ClientAs(FreshTelegramId());
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";

        await user.PostAsJsonAsync("/api/push/subscribe", SubscribeBody(endpoint));
        await user.PostAsJsonAsync("/api/push/subscribe", SubscribeBody(endpoint));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var all = await db.PushSubscriptions.AsNoTracking().ToListAsync();
        all.Should().ContainSingle(s => s.Endpoint == endpoint,
            "повторная подписка того же устройства — upsert по EndpointHash, не новая строка");
    }
}
