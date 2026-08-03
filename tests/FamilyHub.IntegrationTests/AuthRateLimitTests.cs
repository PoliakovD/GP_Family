using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Фабрика с заниженными лимитами rate limiting — основная фабрика наоборот поднимает их,
/// чтобы обычные тесты не ловили 429. Свой контейнер Postgres (наследование фабрики).
/// </summary>
public class RateLimitedWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:AuthPermitLimit", "3");
        builder.UseSetting("RateLimiting:AuthWindowSeconds", "3600");
        builder.UseSetting("RateLimiting:CodePermitLimit", "2");
        builder.UseSetting("RateLimiting:CodeWindowSeconds", "3600");
        // Переопределяет высокий дефолт FamilyHubWebFactory (100000, чтобы остальные тесты не
        // ловили 429) — здесь, наоборот, нужен низкий лимит. Долгое окно (не 5с из прода) —
        // тест не должен зависеть от реального времени выполнения.
        builder.UseSetting("RateLimiting:RedeemPermitLimit", "1");
        builder.UseSetting("RateLimiting:RedeemWindowSeconds", "3600");
    }
}

[CollectionDefinition(Name)]
public class RateLimitCollection : ICollectionFixture<RateLimitedWebFactory>
{
    public const string Name = "RateLimitIntegration";
}

[Collection(RateLimitCollection.Name)]
public class AuthRateLimitTests(RateLimitedWebFactory factory)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static long FreshTelegramId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    private record CreatedFamilyDto(Guid Id);
    private record CreatedInviteDto(Guid Id, string Code);

    [Fact]
    public async Task AuthEndpoints_OverIpLimit_Return429()
    {
        var client = factory.CreateClient();

        // Лимит политики "auth" = 3/час на IP: четвёртый запрос отбрасывается.
        for (var i = 0; i < 3; i++)
            (await client.PostAsJsonAsync("/api/auth/login", new { email = $"u{i}@example.com", password = "Wr0ngPwd" }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await client.PostAsJsonAsync("/api/auth/login", new { email = "u4@example.com", password = "Wr0ngPwd" }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task CodeIssuing_HasOwnStricterLimit()
    {
        var client = factory.CreateClient();

        // Политика "auth-code" = 2/час: третья выдача кода отбрасывается.
        // Отдельная от "auth" партиция — предыдущий тест её не расходует
        // (но и логин-лимит здесь не мешает: у register/start своя политика).
        for (var i = 0; i < 2; i++)
            (await client.PostAsJsonAsync("/api/auth/register/start", new { email = $"rl{i}@example.com" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/auth/register/start", new { email = "rl3@example.com" }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // Регрессия на аудит module-review-2026-08-02/02, находка 2: POST /invites/{code}/redeem —
    // единственный «угадай-секрет» эндпоинт вне /api/auth, раньше вообще без rate-limit.
    [Fact]
    public async Task InviteRedeem_OverIpLimit_Returns429()
    {
        var admin = factory.CreateClientAs(FreshTelegramId());
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = "RL Family" }))
            .Content.ReadFromJsonAsync<CreatedFamilyDto>(JsonOpts);
        var invite = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 10, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreatedInviteDto>(JsonOpts);

        // Лимит политики "invite-redeem" = 1/час на IP: первый погашает, второй (тем же IP,
        // хоть и другим пользователем/кодом — партиция по IP, а не по коду) отбрасывается.
        (await factory.CreateClientAs(FreshTelegramId()).PostAsync($"/api/invites/{invite!.Code}/redeem", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await factory.CreateClientAs(FreshTelegramId()).PostAsync($"/api/invites/{invite.Code}/redeem", null))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
