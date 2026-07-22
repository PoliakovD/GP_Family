using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Задача 2.3: медицинские эндпоинты и дни рождения закрыты до принятия актуальной
/// версии согласия ПДн; принятие открывает доступ; устаревшая версия отклоняется.
/// </summary>
public class ConsentFlowTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record ConsentStatusDto(bool Accepted, string Version);

    [Fact]
    public async Task MedicalEndpoints_WithoutConsent_Return403ConsentRequired_ThenAcceptOpensAccess()
    {
        // Без авто-принятия из ClientAs: чистый пользователь.
        var client = Factory.CreateClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));

        var blocked = await client.GetAsync("/api/medical-records");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("code").GetString().Should().Be("consent_required");

        var status = await client.GetFromJsonAsync<ConsentStatusDto>("/api/consents/status", JsonOpts);
        status!.Accepted.Should().BeFalse();

        AcceptCurrentConsent(client);

        (await client.GetAsync("/api/medical-records")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<ConsentStatusDto>("/api/consents/status", JsonOpts))!
            .Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Accept_StaleVersion_IsRejected()
    {
        var client = Factory.CreateClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));

        var response = await client.PostAsJsonAsync("/api/consents/accept", new { version = "1970-01-01" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("code").GetString().Should().Be("stale_version");
    }

    [Fact]
    public async Task ConsentText_And_PrivacyPolicy_AreAvailableAnonymously()
    {
        var anonymous = AnonymousClient();

        var current = await anonymous.GetAsync("/api/consents/current");
        current.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await current.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("text").GetString().Should().Contain("персональных данных");

        var policy = await anonymous.GetAsync("/api/legal/privacy-policy");
        policy.StatusCode.Should().Be(HttpStatusCode.OK);
        (await policy.Content.ReadAsStringAsync()).Should().Contain("Политика конфиденциальности");
    }

    [Fact]
    public async Task FamilyEndpoints_AreNotConsentGated()
    {
        // Гейт — на обработку медданных (Medical/Birthdays); базовое управление семьёй доступно.
        var client = Factory.CreateClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));

        (await client.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BirthdayEndpoints_AreConsentGated()
    {
        var noConsent = Factory.CreateClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var family = await (await noConsent.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var familyId = family.GetProperty("id").GetGuid();

        (await noConsent.GetAsync($"/api/families/{familyId}/birthdays"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
