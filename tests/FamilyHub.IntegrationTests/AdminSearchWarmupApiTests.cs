using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Считающий вызовы фейковый провайдер поиска — тот же приём, что
/// FakeMedicationSearchProvider в EnrichmentPipelineTests, но с открытым счётчиком: прогрев не
/// вызывает ни одного LLM (см. class doc SearchCacheWarmupJob), только этот провайдер + запись
/// в кэш, поэтому проверять здесь нужно именно число вызовов SearchAsync.</summary>
public sealed class CountingFakeSearchProvider : IMedicationSearchProvider
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);
    public string Name => "FakeProvider";

    public Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        string? specimenDisplayName = null, CancellationToken ct = default,
        WebSearchCallContext? callContext = null)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult<IReadOnlyList<WebSnippet>>(
            [new WebSnippet("Видаль", "https://www.vidal.ru/drugs/test", $"{normalizedName} — тестовый сниппет.")]);
    }
}

public class WarmupWebFactory : AdminWebFactory
{
    public CountingFakeSearchProvider Provider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Регистрация ПОСЛЕ Program.cs (Enrichment:Provider=Null по умолчанию) выигрывает — тот
        // же приём, что EnrichmentWebFactory.
        builder.ConfigureServices(services => services.AddScoped<IMedicationSearchProvider>(_ => Provider));
    }
}

[CollectionDefinition(Name)]
public class WarmupCollection : ICollectionFixture<WarmupWebFactory>
{
    public const string Name = "WarmupIntegration";
}

/// <summary>
/// Прогрев кэша веб-поиска из админки (грантовый лимит облака) — только поиск + запись в кэш,
/// без LLM. Сквозь реальный Postgres/Hangfire (Testcontainers), как и EnrichmentPipelineTests.
/// </summary>
[Collection(WarmupCollection.Name)]
public class AdminSearchWarmupApiTests(WarmupWebFactory factory)
{
    private record WarmupStatusDto(
        Guid? RunId, string? Status, WebSearchTopic? Topic, string? SpecimenDisplayName,
        int TotalNames, int Cursor, int PaidCalls, int SkippedKbHit, int SkippedFreshCache, int Failures,
        int? MaxPaidCalls, DateTime? StartedAt, DateTime? FinishedAt, string? LastError);

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string because, int timeoutMs = 45_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(300);
        }

        (await condition()).Should().BeTrue(because);
    }

    /// <summary>Ждём завершения (в любом терминальном статусе) активного прогона перед следующим
    /// тестом — коллекция шарит один Postgres/Hangfire на всю фабрику, тесты не изолированы друг
    /// от друга иначе (тот же приём, что у остальных Collection-фикстур в этом проекте).</summary>
    private async Task WaitForNoActiveRunAsync(HttpClient client)
    {
        await WaitForAsync(async () =>
        {
            var status = await client.GetFromJsonAsync<WarmupStatusDto>("/api/admin/enrichment/warmup/status");
            return status!.Status is null or "Completed" or "Failed" or "Cancelled";
        }, "предыдущий прогон должен завершиться до начала следующего теста");
    }

    [Fact]
    public async Task Warmup_WithoutSession_Returns401()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/admin/enrichment/warmup", new { topic = 0, names = "парацетамол" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Warmup_LabAnalyteWithoutSpecimen_ReturnsBadRequest()
    {
        var client = await AuthenticatedClientAsync();
        await WaitForNoActiveRunAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/admin/enrichment/warmup", new { topic = 1, specimenKbId = (Guid?)null, names = "гемоглобин" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body!["code"].ToString().Should().Be("specimen_required");
    }

    [Fact]
    public async Task Warmup_BlankNames_ReturnsNothingToDo()
    {
        var client = await AuthenticatedClientAsync();
        await WaitForNoActiveRunAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/admin/enrichment/warmup", new { topic = 0, names = "   \n\n  " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body!["code"].ToString().Should().Be("nothing_to_do");
    }

    [Fact]
    public async Task Warmup_SecondStartWhileRunning_ReturnsConflict()
    {
        var client = await AuthenticatedClientAsync();
        await WaitForNoActiveRunAsync(client);

        // Строка прогона заведена напрямую в БД (Status=Running), не через реальный энкью — так
        // тест проверяет ровно логику AdminSearchWarmupService.StartAsync (и, на случай гонки,
        // уникальный индекс под ней), не зависит от того, когда именно Hangfire реально заберёт
        // задачу из очереди (не проверенная, потенциально короткая задержка опроса — не то, что
        // должен ловить этот тест).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SearchWarmupRuns.Add(new SearchWarmupRun
            {
                Id = Guid.NewGuid(), Topic = WebSearchTopic.Medication, NamesJson = "[]",
                TotalNames = 0, Status = SearchWarmupStatus.Running, StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            "/api/admin/enrichment/warmup", new { topic = 0, names = "парацетамол" });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body!["code"].ToString().Should().Be("already_running");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.SearchWarmupRuns.SingleAsync(r => r.Status == SearchWarmupStatus.Running);
            run.Status = SearchWarmupStatus.Cancelled;
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Warmup_MedicationTopic_SkipsKbHit_PaysOnlyForNewName_WritesCache()
    {
        var client = await AuthenticatedClientAsync();
        await WaitForNoActiveRunAsync(client);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Пробел ПЕРЕД hex-суффиксом обязателен (тот же приём, что в RecalculateIndicatorFlagsJobTests):
        // MedicationNameNormalizer.Normalize (как и WarmupNameParser.Parse, реально нормализующий
        // имена запроса) вызывает LabTextCleanupHelpers.FixMixedScriptHomoglyphs, которая разбирает
        // строку по пробелу и подменяет латинские гомоглифы (a/c/e/…) ТОЛЬКО внутри слова, где уже
        // есть кириллица. Без пробела случайные hex-буквы GUID иногда (не всегда — отсюда и была
        // нестабильность теста, ~1 из 3 прогонов) подменялись бы кириллицей, и сырая строка,
        // посеянная здесь напрямую в БД, расходилась бы с тем, что реально вычислит парсер запроса.
        var alreadyKnown = "тестпрепаратужевсправочнике " + Guid.NewGuid().ToString("N")[..6];
        db.GlobalMedicationsKb.Add(new GlobalMedicationKb
        {
            Id = Guid.NewGuid(), NormalizedName = alreadyKnown, DisplayName = alreadyKnown,
            PayloadJson = "{}", Source = "тест", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var newName = "тестпрепаратновый " + Guid.NewGuid().ToString("N")[..6];
        var callsBefore = factory.Provider.CallCount;

        var start = await client.PostAsJsonAsync(
            "/api/admin/enrichment/warmup", new { topic = 0, names = $"{alreadyKnown}\n{newName}" });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await WaitForAsync(async () =>
        {
            var status = await client.GetFromJsonAsync<WarmupStatusDto>("/api/admin/enrichment/warmup/status");
            return status!.Status == "Completed";
        }, "прогрев на двух коротких именах должен успеть завершиться — фейковый провайдер не ходит в сеть");

        var final = await client.GetFromJsonAsync<WarmupStatusDto>("/api/admin/enrichment/warmup/status");
        final!.PaidCalls.Should().Be(1, "уже известное справочнику название пропускается молча, платится только за новое");
        final.SkippedKbHit.Should().Be(1);
        (factory.Provider.CallCount - callsBefore).Should().Be(1);

        var cacheRow = await db.MedicationSearchCaches.AsNoTracking()
            .FirstOrDefaultAsync(c => c.NormalizedName == newName);
        cacheRow.Should().NotBeNull("прогрев обязан записать сниппеты в кэш — это и есть его смысл");
        cacheRow!.Provider.Should().Be("FakeProvider");

        var alreadyKnownCacheRow = await db.MedicationSearchCaches.AsNoTracking()
            .FirstOrDefaultAsync(c => c.NormalizedName == alreadyKnown);
        alreadyKnownCacheRow.Should().BeNull("KB-хит пропускается ДО платного вызова — строки кэша для него быть не должно");
    }
}
