using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Управление доверенными доменами и кэшем сырых результатов поиска через админку (пересборка
/// enrich-пайплайна) — сквозь реальный Postgres (Testcontainers), т.к. доверенные домены теперь
/// БД-backed (раньше были статикой в appsettings, юнит-тестами не покрывались вовсе).
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class AdminEnrichmentApiTests(AdminWebFactory factory)
{
    private record TrustedDomainDto(Guid Id, string Domain, int Rank, bool IsEnabled);

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task TrustedDomains_WithoutSession_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/admin/enrichment/trusted-domains?topic=Medication");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TrustedDomains_SeededDefaults_AreListedForEachTopic()
    {
        var client = await AuthenticatedClientAsync();

        var medication = await client.GetFromJsonAsync<List<TrustedDomainDto>>(
            "/api/admin/enrichment/trusted-domains?topic=Medication");
        var labAnalyte = await client.GetFromJsonAsync<List<TrustedDomainDto>>(
            "/api/admin/enrichment/trusted-domains?topic=LabAnalyte");

        medication.Should().Contain(d => d.Domain == "vidal.ru");
        labAnalyte.Should().Contain(d => d.Domain == "invitro.ru");
        // Порядок значим для LabAnalyte (ReferenceRangeMerger) — invitro.ru приоритетнее gemotest.ru.
        labAnalyte!.OrderBy(d => d.Rank).First().Domain.Should().Be("invitro.ru");
    }

    [Fact]
    public async Task AddDomain_ThenToggleDisabled_ThenDelete_FullLifecycle()
    {
        var client = await AuthenticatedClientAsync();
        var uniqueDomain = $"test-{Guid.NewGuid():N}.example";

        // topic — числом (0=Medication), не строкой: JSON-тело не настроено на JsonStringEnumConverter,
        // тот же формат, что и остальные enum-поля запросов в этом проекте (см. InviteEndpoints.CreateInviteRequest).
        var addResponse = await client.PostAsJsonAsync("/api/admin/enrichment/trusted-domains",
            new { topic = 0, domain = uniqueDomain });
        addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var added = await addResponse.Content.ReadFromJsonAsync<TrustedDomainDto>();
        added!.IsEnabled.Should().BeTrue();

        var disableResponse = await client.PutAsJsonAsync(
            $"/api/admin/enrichment/trusted-domains/{added.Id}", new { isEnabled = false });
        disableResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDisable = await client.GetFromJsonAsync<List<TrustedDomainDto>>(
            "/api/admin/enrichment/trusted-domains?topic=Medication");
        afterDisable.Should().Contain(d => d.Id == added.Id && !d.IsEnabled);

        var deleteResponse = await client.DeleteAsync($"/api/admin/enrichment/trusted-domains/{added.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDelete = await client.GetFromJsonAsync<List<TrustedDomainDto>>(
            "/api/admin/enrichment/trusted-domains?topic=Medication");
        afterDelete.Should().NotContain(d => d.Id == added.Id);
    }

    [Fact]
    public async Task AddDomain_DuplicateInSameTopic_ReturnsConflict()
    {
        var client = await AuthenticatedClientAsync();

        var first = await client.PostAsJsonAsync("/api/admin/enrichment/trusted-domains",
            new { topic = 1, domain = "vidal.ru" }); // 1=LabAnalyte; vidal.ru уже есть у Medication — другая тема, ок
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var duplicate = await client.PostAsJsonAsync("/api/admin/enrichment/trusted-domains",
            new { topic = 1, domain = "vidal.ru" });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SearchCache_UnknownTopicQuery_ReturnsEmptyList_NotError()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync(
            $"/api/admin/enrichment/search-cache?topic=Medication&query=никогда-не-искали-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SearchCacheListDto>();
        body!.Rows.Should().BeEmpty();
        body.Total.Should().Be(0);
    }

    [Fact]
    public async Task SearchCache_DetailForUnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/admin/enrichment/search-cache/{Guid.NewGuid()}?topic=Medication");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record SearchCacheListDto(List<object> Rows, int Total);
}
