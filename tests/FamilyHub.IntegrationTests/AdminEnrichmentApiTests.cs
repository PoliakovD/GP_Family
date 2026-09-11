using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private record PurgeResponseDto(int DeletedCount);

    private async Task<Guid> SeedLabAnalyteSearchCacheAsync(string normalizedName, Guid specimenKbId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.LabAnalyteSearchCaches.Add(new LabAnalyteSearchCache
        {
            Id = id,
            NormalizedName = normalizedName,
            SpecimenKbId = specimenKbId,
            Provider = "тест",
            LastUpdatedAt = now,
            CanBeUpdatedAfter = now,
            SnippetsJson = "[]",
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task PurgeUnresolvedSpecimenSearchCache_DeletesOnlyUnresolvedRows()
    {
        var client = await AuthenticatedClientAsync();
        var unresolvedName = $"показатель-unresolved-{Guid.NewGuid():N}";
        var resolvedName = $"показатель-resolved-{Guid.NewGuid():N}";

        var unresolvedId = await SeedLabAnalyteSearchCacheAsync(unresolvedName, SpecimenContextIds.Unresolved);
        using (var scope = factory.Services.CreateScope())
        {
            var specimens = scope.ServiceProvider.GetRequiredService<Modules.Medical.Extraction.GlobalSpecimenKbService>();
            var bloodId = await specimens.FindOrRegisterAsync($"Кровь {Guid.NewGuid():N}", $"кровь{Guid.NewGuid():N}");
            await SeedLabAnalyteSearchCacheAsync(resolvedName, bloodId);
        }

        var response = await client.PostAsync("/api/admin/enrichment/search-cache/lab-analytes/purge-unresolved-specimen", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PurgeResponseDto>();
        body!.DeletedCount.Should().BeGreaterThanOrEqualTo(1);

        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await checkDb.LabAnalyteSearchCaches.AnyAsync(c => c.Id == unresolvedId)).Should().BeFalse();
        (await checkDb.LabAnalyteSearchCaches.AnyAsync(c => c.NormalizedName == resolvedName)).Should().BeTrue();
    }

    // --- Полное редактирование/удаление строки кэша ---

    private record SearchCacheSnippetDto(string Title, string Url, string Text, string? Domain, bool IsTrustedByDomain, bool? Override, bool Enabled);
    private record SearchCacheDetailDto(Guid Id, string NormalizedName, string? Specimen, string Provider, DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter, List<SearchCacheSnippetDto> Snippets);

    [Fact]
    public async Task UpdateSearchCache_ReplacesSnippetsWholesale_AndPrunesOverridesForRemovedUrls()
    {
        var client = await AuthenticatedClientAsync();
        var normalizedName = $"кэштест{Guid.NewGuid():N}";
        var oldUrl = "https://vidal.ru/old";
        var keptUrl = "https://vidal.ru/kept";

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.MedicationSearchCaches.Add(new MedicationSearchCache
            {
                Id = id, NormalizedName = normalizedName, Provider = "тест", LastUpdatedAt = now, CanBeUpdatedAfter = now,
                SnippetsJson = $$"""[{"title":"Старый","url":"{{oldUrl}}","text":"текст"},{"title":"Оставить","url":"{{keptUrl}}","text":"текст"}]""",
                OverridesJson = $$"""{"{{oldUrl}}":true,"{{keptUrl}}":false}""",
            });
            await db.SaveChangesAsync();
        }

        // Убираем oldUrl, добавляем новый, keptUrl остаётся с изменённым текстом — тот же
        // приём, что payload-редактор: весь список пересылается целиком.
        var newUrl = "https://rlsnet.ru/new";
        var updateResponse = await client.PutAsJsonAsync($"/api/admin/enrichment/search-cache/{id}", new
        {
            topic = 0,
            provider = "обновлённый-провайдер",
            snippets = new object[]
            {
                new { title = "Оставить (правка)", url = keptUrl, text = "новый текст" },
                new { title = "Новый", url = newUrl, text = "текст" },
            },
        });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var detail = await client.GetFromJsonAsync<SearchCacheDetailDto>($"/api/admin/enrichment/search-cache/{id}?topic=0");
        detail!.Provider.Should().Be("обновлённый-провайдер");
        detail.Snippets.Should().HaveCount(2);
        detail.Snippets.Should().NotContain(s => s.Url == oldUrl);
        detail.Snippets.Should().Contain(s => s.Url == keptUrl && s.Text == "новый текст");
        detail.Snippets.Single(s => s.Url == keptUrl).Override.Should().Be(false, "override уцелевшего URL должен пережить редактирование остального списка");
        detail.Snippets.Single(s => s.Url == newUrl).Override.Should().BeNull("у нового сниппета не может быть override, которого никто не ставил");

        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await checkDb.MedicationSearchCaches.AsNoTracking().SingleAsync(c => c.Id == id);
        row.OverridesJson.Should().NotContain(oldUrl, "override удалённого сниппета должен вычищаться, а не висеть мёртвым грузом");
    }

    [Fact]
    public async Task UpdateSearchCache_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync($"/api/admin/enrichment/search-cache/{Guid.NewGuid()}",
            new { topic = 0, provider = (string?)null, snippets = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSearchCache_EmptyUrl_ReturnsBadRequest()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedLabAnalyteSearchCacheAsync($"пустаяссылка{Guid.NewGuid():N}", SpecimenContextIds.Unresolved);

        var response = await client.PutAsJsonAsync($"/api/admin/enrichment/search-cache/{id}", new
        {
            topic = 1,
            provider = (string?)null,
            snippets = new object[] { new { title = "Т", url = "", text = "текст" } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteSearchCache_KnownId_RemovesRow_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedLabAnalyteSearchCacheAsync($"удалитькэш{Guid.NewGuid():N}", SpecimenContextIds.Unresolved);

        var missingResponse = await client.DeleteAsync($"/api/admin/enrichment/search-cache/{Guid.NewGuid()}?topic=1");
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var deleteResponse = await client.DeleteAsync($"/api/admin/enrichment/search-cache/{id}?topic=1");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await checkDb.LabAnalyteSearchCaches.AnyAsync(c => c.Id == id)).Should().BeFalse();
    }
}
