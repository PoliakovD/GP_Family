using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>AdminWebFactory, но с обязательным вторым фактором — как на проде.</summary>
public class AdminTotpWebFactory : AdminWebFactory
{
    public const string TotpSecretBase32 = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Admin:RequireTotp", "true");
        builder.UseSetting("Admin:TotpSecret", TotpSecretBase32);
    }

    /// <summary>Код для шага со смещением от текущего (±1 допускается сервером).</summary>
    public static string Code(int stepOffset = 0) =>
        Totp.Compute(Totp.DecodeBase32(TotpSecretBase32)!, Totp.TimeStep(DateTimeOffset.UtcNow) + stepOffset);
}

[CollectionDefinition(Name)]
public class AdminTotpCollection : ICollectionFixture<AdminTotpWebFactory>
{
    public const string Name = "FamilyHub admin TOTP tests";
}

/// <summary>
/// Второй фактор и сессии админ-панели в БД (docs/security/security-audit-2026-10.md, бэклог M3).
/// Тесты в коллекции идут последовательно; каждый успешный вход «съедает» шаг TOTP, поэтому
/// следующие используют более поздний шаг (сервер принимает текущий ±1 и только по возрастанию).
/// </summary>
[Collection(AdminTotpCollection.Name)]
public class AdminTotpApiTests(AdminTotpWebFactory factory)
{
    private static object Login(string? totp) =>
        new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword, totp };

    [Theory]
    [InlineData(null)]
    [InlineData("000000")]
    [InlineData("abc")]
    public async Task Login_WithoutValidTotp_Returns401(string? totp)
    {
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/admin/session", Login(totp))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithTotp_Works_SameCodeTwice_Rejected_RevokeAll_KillsEverySession()
    {
        // Шаг +1: предыдущие тесты могли использовать текущий шаг.
        var code = AdminTotpWebFactory.Code(stepOffset: 1);
        var deviceA = factory.CreateClient();
        var login = await deviceA.PostAsJsonAsync("/api/admin/session", Login(code));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var stolenCookie = login.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("familyhub.admin=", StringComparison.Ordinal)).Split(';')[0];

        (await factory.CreateClient().PostAsJsonAsync("/api/admin/session", Login(code)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "код TOTP одноразовый");

        (await deviceA.PostAsync("/api/admin/session/revoke-all", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var attacker = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var replay = new HttpRequestMessage(HttpMethod.Get, "/api/admin/session");
        replay.Headers.Add("Cookie", stolenCookie);
        (await attacker.SendAsync(replay)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "«выйти везде» отзывает все сессии");
    }
}
