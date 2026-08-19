using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests.Bot;

/// <summary>
/// /internal/bot/* — контракт между FamilyHub.TelegramBot и FamilyHub.Api (ADR-0008). Бьёт по
/// эндпоинтам напрямую (не через хост бота — тот покрыт BotWebhookTests/TelegramLinkFlowTests),
/// чтобы изолированно проверить сам гейт (InternalBotAuthFilter) и инвариант lookup-only,
/// который раньше проверялся в BotWebhookTests, а теперь живёт на границе Api.
/// </summary>
[Collection(BotIntegrationCollection.Name)]
public class InternalBotEndpointsTests(BotIntegrationFixture fixture)
{
    private HttpClient Client(string? token)
    {
        var client = fixture.CreateApiClient();
        if (token is not null)
            client.DefaultRequestHeaders.Add("X-Internal-Token", token);
        return client;
    }

    [Fact]
    public async Task Ping_WithoutToken_Returns401()
    {
        var response = await Client(null).GetAsync("/internal/bot/ping");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ping_WrongToken_Returns401()
    {
        var response = await Client("not-the-real-token").GetAsync("/internal/bot/ping");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ping_CorrectToken_Returns200()
    {
        var response = await Client(BotIntegrationFixture.InternalToken).GetAsync("/internal/bot/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResolveUser_UnknownTelegramId_ReturnsNotLinked()
    {
        var response = await Client(BotIntegrationFixture.InternalToken)
            .PostAsJsonAsync("/internal/bot/users/resolve", new { telegramId = 999_111 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ResolveResponseDto>();
        body!.IsLinked.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveUser_KnownTelegramId_ReturnsLinked()
    {
        const long telegramId = 999_222;
        using (var scope = fixture.ApiServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                TelegramId = telegramId,
                DisplayName = "Resolved",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await Client(BotIntegrationFixture.InternalToken)
            .PostAsJsonAsync("/internal/bot/users/resolve", new { telegramId });

        var body = await response.Content.ReadFromJsonAsync<ResolveResponseDto>();
        body!.IsLinked.Should().BeTrue();
    }

    [Fact]
    public async Task RedeemInvite_UnlinkedTelegramId_ReturnsNotLinked_AndCreatesNoFamilyMember()
    {
        // Lookup-only: без привязанного TelegramId /internal/bot/invites/redeem возвращает
        // NotLinked, даже не заглядывая в саму запись инвайта — тот же инвариант, что раньше
        // проверял BotWebhookTests на уровне вебхука. Проверяем на конкретно СВОЕЙ семье (не
        // глобальным COUNT по FamilyMembers), т.к. Postgres в этом фикстуре общий на всю
        // коллекцию тестов.
        using var setupScope = fixture.ApiServices.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (family, _) = setupDb.SeedFamilyWithAdmin();
        await setupDb.SaveChangesAsync();

        var response = await Client(BotIntegrationFixture.InternalToken)
            .PostAsJsonAsync("/internal/bot/invites/redeem", new { code = "does-not-matter", telegramId = 999_333 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RedeemResponseDto>();
        body!.Outcome.Should().Be("NotLinked");

        using var scope = fixture.ApiServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.FamilyMembers.CountAsync(m => m.FamilyId == family.Id)).Should().Be(1, "остаётся только сидированный admin");
    }

    private record ResolveResponseDto(bool IsLinked);
    private record RedeemResponseDto(string Outcome);
}
