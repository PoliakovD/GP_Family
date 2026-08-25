using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Веб-поиск, отвечающий одним доверенным сниппетом независимо от запроса — детерминизм
/// без реального Brave API/сети (см. ADR-0005: провайдер подключается за интерфейсом именно
/// затем, чтобы конвейер можно было проверить без реального внешнего вызова).</summary>
file sealed class FakeMedicationSearchProvider : IMedicationSearchProvider
{
    public string Name => "FakeProvider";

    public Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WebSnippet>>(
        [
            new WebSnippet(
                "Видаль",
                "https://www.vidal.ru/drugs/test",
                $"{normalizedName} — тестовое обезболивающее и жаропонижающее средство для интеграционного теста."),
        ]);
}

/// <summary>Возвращает фиксированный корректный ответ суммаризации — заменяет локальный Qwen,
/// который недоступен в CI. Ссылается на источник [0] — проходит антигаллюцинационный гейт.</summary>
file sealed class FakeLmStudioJsonClient : ILmStudioJsonClient
{
    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default) =>
        ExtractJsonAsync(systemPrompt, userText, ct);

    public Task<LmStudioJsonResult> ExtractJsonAsync(string systemPrompt, string userText, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["internationalName"] = JsonSerializer.SerializeToElement("Тестовое МНН"),
            ["tradeNames"] = JsonSerializer.SerializeToElement(new[] { "Тестпрепарат" }),
            ["form"] = JsonSerializer.SerializeToElement("таблетки"),
            ["purpose"] = JsonSerializer.SerializeToElement("жаропонижающее"),
            ["storage"] = JsonSerializer.SerializeToElement((string?)null),
            ["driving"] = JsonSerializer.SerializeToElement((string?)null),
            ["specialNotes"] = JsonSerializer.SerializeToElement((string?)null),
            ["usedSourceIndexes"] = JsonSerializer.SerializeToElement(new[] { 0 }),
        };
        return Task.FromResult(new LmStudioJsonResult(true, payload, null));
    }
}

/// <summary>Полный happy-path конвейера (фейковые провайдер поиска и LLM — см. выше) + дедуп
/// конкурентных запросов одного и того же препарата.</summary>
public class EnrichmentWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Регистрация ПОСЛЕ Program.cs (Enrichment:Provider=Null по умолчанию → NullMedicationSearchProvider)
        // выигрывает — тот же приём, что CapturingEmailSender в базовой фабрике.
        builder.ConfigureServices(services =>
        {
            services.AddScoped<IMedicationSearchProvider, FakeMedicationSearchProvider>();
            services.AddScoped<ILmStudioJsonClient, FakeLmStudioJsonClient>();
        });
    }
}

/// <summary>Тот же конвейер, но с нулевой месячной квотой — первая же задача уходит в Skipped,
/// без реального внешнего запроса.</summary>
public class EnrichmentQuotaZeroWebFactory : EnrichmentWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Enrichment:MonthlyQuota", "0");
    }
}

/// <summary>Возвращает correctedName, когда видит конкретное "искажённое OCR" название в userText —
/// имитирует случай, когда фото упаковки распознано с ошибкой, но найденные источники явно
/// указывают на настоящий препарат (см. MedicationEnrichmentProcessor.ResolveCorrectedName).</summary>
file sealed class FakeCorrectingLmStudioJsonClient : ILmStudioJsonClient
{
    public const string GarbledName = "Сумматрептан";
    public const string CorrectedName = "Суматриптан";

    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default) =>
        ExtractJsonAsync(systemPrompt, userText, ct);

    public Task<LmStudioJsonResult> ExtractJsonAsync(string systemPrompt, string userText, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["internationalName"] = JsonSerializer.SerializeToElement(CorrectedName),
            ["tradeNames"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
            ["form"] = JsonSerializer.SerializeToElement("таблетки"),
            ["purpose"] = JsonSerializer.SerializeToElement("от мигрени"),
            ["usage"] = JsonSerializer.SerializeToElement((string?)null),
            ["storage"] = JsonSerializer.SerializeToElement((string?)null),
            ["driving"] = JsonSerializer.SerializeToElement((string?)null),
            ["specialNotes"] = JsonSerializer.SerializeToElement((string?)null),
            ["usedSourceIndexes"] = JsonSerializer.SerializeToElement(new[] { 0 }),
            ["correctedName"] = userText.Contains(GarbledName, StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.SerializeToElement(CorrectedName)
                : JsonSerializer.SerializeToElement((string?)null),
        };
        return Task.FromResult(new LmStudioJsonResult(true, payload, null));
    }
}

public class EnrichmentCorrectionWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddScoped<IMedicationSearchProvider, FakeMedicationSearchProvider>();
            services.AddScoped<ILmStudioJsonClient, FakeCorrectingLmStudioJsonClient>();
        });
    }
}

[CollectionDefinition(Name)]
public class EnrichmentCollection : ICollectionFixture<EnrichmentWebFactory>
{
    public const string Name = "EnrichmentIntegration";
}

[CollectionDefinition(Name)]
public class EnrichmentQuotaZeroCollection : ICollectionFixture<EnrichmentQuotaZeroWebFactory>
{
    public const string Name = "EnrichmentQuotaZeroIntegration";
}

[CollectionDefinition(Name)]
public class EnrichmentCorrectionCollection : ICollectionFixture<EnrichmentCorrectionWebFactory>
{
    public const string Name = "EnrichmentCorrectionIntegration";
}

/// <summary>Тот же happy-path конвейер, но с подменённым IBackgroundJobClient, у которого
/// Create(...) всегда бросает исключение — имитирует недоступность Hangfire-стораж
/// (см. аудит module-review-2026-08-02/04-medications-medkits-kb-enrichment-ocr.md, находка 1).
/// Регистрация ПОСЛЕ Program.cs (который регистрирует настоящий AddHangfireServer/клиент)
/// выигрывает — тот же приём, что и остальные подмены в этом файле.</summary>
public class EnrichmentHangfireDownWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            var client = Substitute.For<IBackgroundJobClient>();
            client.Create(Arg.Any<Job>(), Arg.Any<IState>())
                .Returns(_ => throw new InvalidOperationException("Hangfire storage unavailable (test)"));
            services.AddSingleton(client);
        });
    }
}

[CollectionDefinition(Name)]
public class EnrichmentHangfireDownCollection : ICollectionFixture<EnrichmentHangfireDownWebFactory>
{
    public const string Name = "EnrichmentHangfireDownIntegration";
}

/// <summary>Классы этого файла НЕ наследуют IntegrationTestBase (у неё свой [Collection] на
/// FamilyHubWebFactory, а здесь свои производные фабрики) — согласие ПДн принимается тем же
/// способом, что и в OutboxFailureIsolationTests: напрямую, без общего protected-хелпера.</summary>
file static class ConsentHelper
{
    public static void AcceptCurrent(HttpClient client)
    {
        var current = client.GetFromJsonAsync<Dictionary<string, JsonElement>>("/api/consents/current")
            .GetAwaiter().GetResult();
        client.PostAsJsonAsync("/api/consents/accept", new { version = current!["version"].GetString() })
            .GetAwaiter().GetResult().EnsureSuccessStatusCode();
    }
}

[Collection(EnrichmentCollection.Name)]
public class EnrichmentPipelineTests(EnrichmentWebFactory factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record MedicationKbCardDto(string DisplayName, string Source);
    private record MedicationKbResponseDto(int Status, MedicationKbCardDto? Card);
    private record NotificationItemDto(Guid Id, NotificationType Type, string Title);
    private record RefreshOutcomeDto(int Status, DateTime? AvailableAt);

    private const int StatusReady = 4;
    private const int RefreshStatusRequested = 0;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Уникальный суффикс без цифр для названий препаратов, доходящих до KbWriter —
    /// сырой Guid.NewGuid():N время от времени содержит подряд идущие 7+ цифр (телефон/паспорт
    /// эвристика в KbWriter.FindPersonalContextViolation, см. LongDigitsPattern) и КОРРЕКТНО, но
    /// НЕПРЕДНАМЕРЕННО отклоняет запись в справочник — задача зависает не в Ready, тест валится по
    /// таймауту. Та же цифра всегда даёт ту же букву (g-p, без пересечения с a-f самого hex) —
    /// уникальность и воспроизводимость сохраняются, просто цифр в строке больше никогда нет.</summary>
    private static string UniqueDrugNameSuffix() => new(Guid.NewGuid().ToString("N")
        .Select(c => char.IsDigit(c) ? (char)('g' + (c - '0')) : c).ToArray());

    private HttpClient ClientAs(long telegramId)
    {
        var client = factory.CreateClientAs(telegramId);
        ConsentHelper.AcceptCurrent(client);
        return client;
    }

    private async Task<Guid> CreateFamilyAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        return body!.Id;
    }

    private async Task<Guid> CreateMedkitAsync(HttpClient admin, Guid familyId)
    {
        var response = await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits", new CreateMedkitRequest("Аптечка"));
        var body = await response.Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        return body!.Id;
    }

    private async Task<Guid> CreateMedicationAsync(HttpClient admin, Guid medkitId, string name)
    {
        var response = await admin.PostAsJsonAsync($"/api/medkits/{medkitId}/medications", new CreateMedicationRequest(name, null, null));
        var body = await response.Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);
        return body!.Id;
    }

    private static async Task<MedicationKbResponseDto> GetKbStatusAsync(HttpClient client, Guid medicationId)
    {
        var response = await client.GetAsync($"/api/medications/{medicationId}/kb");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MedicationKbResponseDto>(JsonOpts))!;
    }

    /// <summary>Полинг до выполнения условия — конвейер асинхронный, тот же приём, что WaitForAsync в OutboxEventFlowTests.</summary>
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

    [Fact]
    public async Task SavedMedication_GetsEnrichedAsynchronously_AndNotifiesRequester()
    {
        var admin = ClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);

        var medicationId = await CreateMedicationAsync(admin, medkitId, $"Тестовыйпрепарат{UniqueDrugNameSuffix()}");

        await WaitForAsync(
            async () => (await GetKbStatusAsync(admin, medicationId)).Status == StatusReady,
            "фоновый конвейер (Hangfire, очередь enrichment) должен довести задачу до Completed");

        var status = await GetKbStatusAsync(admin, medicationId);
        status.Card!.Source.Should().Contain("FakeProvider");

        // Доставка события MedicationEnrichedEvent теперь асинхронная (ADR-0006, нет
        // форсирующего /dev/trigger-outbox-dispatch) — ждём эффект тем же полингом ниже.
        await WaitForAsync(async () =>
        {
            var notifications = await (await admin.GetAsync("/api/notifications"))
                .Content.ReadFromJsonAsync<List<NotificationItemDto>>(JsonOpts);
            return notifications!.Any(n => n.Type == NotificationType.MedicationEnriched);
        }, "пользователь, сохранивший медикамент, должен получить уведомление о пополнении справочника");
    }

    [Fact]
    public async Task ManualRefresh_RightAfterEnrichment_ReusesCachedSnippets_WithoutNewExternalCall()
    {
        var admin = ClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);

        var medicationId = await CreateMedicationAsync(admin, medkitId, $"Кэшпрепарат{UniqueDrugNameSuffix()}");

        await WaitForAsync(
            async () => (await GetKbStatusAsync(admin, medicationId)).Status == StatusReady,
            "фоновый конвейер должен довести задачу до Completed, записав MedicationSearchCache");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var medication = await db.Medications.AsNoTracking().SingleAsync(m => m.Id == medicationId);
        var normalizedName = MedicationNameNormalizer.Normalize(medication.Name);

        var cacheRow = await db.MedicationSearchCaches.AsNoTracking()
            .SingleAsync(c => c.NormalizedName == normalizedName);
        cacheRow.CanBeUpdatedAfter.Should().BeAfter(DateTime.UtcNow,
            "успешный внешний запрос должен сразу поставить минимум +1 месяц кулдауна (Enrichment:MinRefreshIntervalMonths)");
        cacheRow.SnippetsJson.Should().NotBeNullOrEmpty(
            "настоящий кэш хранит сами сниппеты, а не только факт обращения — иначе пересчитать summarize без нового платного запроса нечем");
        var lastUpdatedBefore = cacheRow.LastUpdatedAt;

        // Ручной «Уточнить в справочнике» сразу после первого обогащения — то, что раньше упиралось
        // в кулдаун и полностью блокировалось. Теперь задача должна выполниться, переиспользовав
        // закэшированные сниппеты, без нового обращения к платному API.
        var refreshResponse = await admin.PostAsync($"/api/medications/{medicationId}/kb/refresh", null);
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var outcome = await refreshResponse.Content.ReadFromJsonAsync<RefreshOutcomeDto>(JsonOpts);
        outcome!.Status.Should().Be(RefreshStatusRequested);

        await WaitForAsync(async () =>
        {
            var completedCount = await db.MedicationEnrichmentJobs
                .CountAsync(j => j.NormalizedName == normalizedName && j.Status == EnrichmentJobStatus.Completed);
            return completedCount >= 2;
        }, "ручной рефреш должен поставить и выполнить вторую задачу, переиспользовав кэш");

        var cacheRowAfter = await db.MedicationSearchCaches.AsNoTracking()
            .SingleAsync(c => c.NormalizedName == normalizedName);
        cacheRowAfter.LastUpdatedAt.Should().Be(lastUpdatedBefore,
            "в пределах кулдауна повторный рефреш не должен был обращаться к платному API — кэш не обновился");

        var jobsForName = await db.MedicationEnrichmentJobs.Where(j => j.NormalizedName == normalizedName).ToListAsync();
        jobsForName.Should().HaveCount(2);
        jobsForName.Count(j => j.ExternalSearchAt != null).Should().Be(1,
            "только первая (исходная) задача должна была реально сходить к платному API — вторая переиспользовала кэш");
    }

    [Fact]
    public async Task ConcurrentSaves_OfSameDrugName_ProduceAtMostOneEnrichmentJob()
    {
        var admin1 = ClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var admin2 = ClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var family1 = await CreateFamilyAsync(admin1);
        var family2 = await CreateFamilyAsync(admin2);
        var medkit1 = await CreateMedkitAsync(admin1, family1);
        var medkit2 = await CreateMedkitAsync(admin2, family2);

        var sharedName = $"Одинаковыйпрепарат{UniqueDrugNameSuffix()}";

        // Два независимых пользователя из разных семей сохраняют препарат с одинаковым названием
        // одновременно — дедуп по частичному уникальному индексу NormalizedName (Pending/Running)
        // должен не дать создаться второй задаче (см. MedicationEnrichmentJobConfiguration).
        await Task.WhenAll(
            CreateMedicationAsync(admin1, medkit1, sharedName),
            CreateMedicationAsync(admin2, medkit2, sharedName));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalizedName = MedicationNameNormalizer.Normalize(sharedName);

        await WaitForAsync(async () =>
        {
            var jobCount = await db.MedicationEnrichmentJobs.CountAsync(j => j.NormalizedName == normalizedName);
            return jobCount >= 1;
        }, "хотя бы одна задача должна была создаться");

        (await db.MedicationEnrichmentJobs.CountAsync(j => j.NormalizedName == normalizedName))
            .Should().Be(1, "конкурентное сохранение одного и того же препарата не должно порождать вторую задачу/второй внешний запрос");
    }
}

[Collection(EnrichmentQuotaZeroCollection.Name)]
public class EnrichmentQuotaTests(EnrichmentQuotaZeroWebFactory factory)
{
    private record CreateFamilyResponseDto(Guid Id);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private HttpClient ClientAs(long telegramId)
    {
        var client = factory.CreateClientAs(telegramId);
        ConsentHelper.AcceptCurrent(client);
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

    [Fact]
    public async Task MonthlyQuotaExhausted_JobIsSkipped_WithoutExternalCall()
    {
        var admin = ClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var medkit = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/medkits", new CreateMedkitRequest("Аптечка")))
            .Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        var medication = await (await admin.PostAsJsonAsync($"/api/medkits/{medkit!.Id}/medications",
                new CreateMedicationRequest($"Квотныйпрепарат{Guid.NewGuid():N}", null, null)))
            .Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await WaitForAsync(async () =>
        {
            var job = await db.MedicationEnrichmentJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.MedicationId == medication!.Id);
            return job is { Status: EnrichmentJobStatus.Skipped };
        }, "с MonthlyQuota=0 первая же задача должна быть Skipped, а не уйти во внешний поиск");

        var completedJob = await db.MedicationEnrichmentJobs.AsNoTracking()
            .SingleAsync(j => j.MedicationId == medication!.Id);
        completedJob.ExternalSearchAt.Should().BeNull("исчерпанная квота проверяется ДО внешнего запроса");
        completedJob.Error.Should().Contain("квота");
    }
}

[Collection(EnrichmentCorrectionCollection.Name)]
public class EnrichmentNameCorrectionTests(EnrichmentCorrectionWebFactory factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record MedicationKbCardDto(string DisplayName);
    private record MedicationKbResponseDto(int Status, MedicationKbCardDto? Card);

    private const int StatusReady = 4;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private HttpClient ClientAs(long telegramId)
    {
        var client = factory.CreateClientAs(telegramId);
        ConsentHelper.AcceptCurrent(client);
        return client;
    }

    private async Task<Guid> CreateFamilyAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        return body!.Id;
    }

    private async Task<Guid> CreateMedkitAsync(HttpClient admin, Guid familyId)
    {
        var response = await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits", new CreateMedkitRequest("Аптечка"));
        var body = await response.Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        return body!.Id;
    }

    private async Task<Guid> CreateMedicationAsync(HttpClient admin, Guid medkitId, string name)
    {
        var response = await admin.PostAsJsonAsync($"/api/medkits/{medkitId}/medications", new CreateMedicationRequest(name, null, null));
        var body = await response.Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);
        return body!.Id;
    }

    private static async Task<MedicationKbResponseDto> GetKbStatusAsync(HttpClient client, Guid medicationId)
    {
        var response = await client.GetAsync($"/api/medications/{medicationId}/kb");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MedicationKbResponseDto>(JsonOpts))!;
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

    [Fact]
    public async Task OcrMisreadName_GetsCorrectedInKnowledgeBase_AndOriginalResolvesViaAliasWithoutNewJob()
    {
        var admin = ClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);

        var medicationId = await CreateMedicationAsync(admin, medkitId, FakeCorrectingLmStudioJsonClient.GarbledName);

        await WaitForAsync(
            async () => (await GetKbStatusAsync(admin, medicationId)).Status == StatusReady,
            "конвейер должен довести задачу до Completed");

        var status = await GetKbStatusAsync(admin, medicationId);
        status.Card!.DisplayName.Should().Be(FakeCorrectingLmStudioJsonClient.CorrectedName,
            "суммаризатор нашёл настоящее название в цитируемых источниках — справочник должен хранить его, а не искажённое OCR-имя");

        // Второй медикамент с ТЕМ ЖЕ искажённым именем — должен разрешиться сразу через алиас на
        // исправленной записи, без повторной постановки задачи/внешнего запроса.
        var medkit2Id = await CreateMedkitAsync(admin, familyId);
        var secondMedicationId = await CreateMedicationAsync(admin, medkit2Id, FakeCorrectingLmStudioJsonClient.GarbledName);

        await WaitForAsync(
            async () => (await GetKbStatusAsync(admin, secondMedicationId)).Status == StatusReady,
            "повторное искажённое имя должно резолвиться немедленно через алиас, без ожидания фонового конвейера");

        var secondStatus = await GetKbStatusAsync(admin, secondMedicationId);
        secondStatus.Card!.DisplayName.Should().Be(FakeCorrectingLmStudioJsonClient.CorrectedName);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var garbledNormalized = MedicationNameNormalizer.Normalize(FakeCorrectingLmStudioJsonClient.GarbledName);

        (await db.MedicationEnrichmentJobs.CountAsync(j => j.NormalizedName == garbledNormalized))
            .Should().Be(1, "повторное искажённое имя не должно порождать вторую задачу — алиас должен сработать до постановки в очередь");
    }
}

// Регрессия на аудит module-review-2026-08-02/04, находка 1: сбой постановки задачи обогащения
// (Hangfire недоступен) раньше пробрасывался необработанным исключением через
// MedicationService.CreateAsync до самого эндпоинта → 500 клиенту, хотя медикамент уже был
// закоммичен отдельным, более ранним SaveChangesAsync. Пользователь увидел бы ошибку и,
// вероятно, повторил попытку — дубль медикамента при том, что первая попытка на самом деле
// удалась.
[Collection(EnrichmentHangfireDownCollection.Name)]
public class EnrichmentHangfireDownTests(EnrichmentHangfireDownWebFactory factory)
{
    private record CreateFamilyResponseDto(Guid Id);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private HttpClient ClientAs(long telegramId)
    {
        var client = factory.CreateClientAs(telegramId);
        ConsentHelper.AcceptCurrent(client);
        return client;
    }

    [Fact]
    public async Task CreateMedication_WhenHangfireEnqueueFails_StillReturns201_AndLeavesNoOrphanedPendingJob()
    {
        var admin = ClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var medkit = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/medkits", new CreateMedkitRequest("Аптечка")))
            .Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);

        var name = $"Хангфайрнедоступен{Guid.NewGuid():N}";
        var createResponse = await admin.PostAsJsonAsync(
            $"/api/medkits/{medkit!.Id}/medications", new CreateMedicationRequest(name, null, null));

        // Главное утверждение находки: медикамент создаётся успешно, а не 500, несмотря на то,
        // что Hangfire-энкью внутри гарантированно бросает исключение (см. фабрику).
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var medication = await createResponse.Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);
        medication!.Name.Should().Be(name);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalizedName = MedicationNameNormalizer.Normalize(name);

        // Pending-строка должна быть откачена вместе с несостоявшимся Hangfire-энкью — иначе
        // дедуп по NormalizedName навсегда заблокировал бы будущие попытки обогащения этого
        // препарата (Hangfire ведь так и не узнал о задаче, но строка выглядела бы "в очереди").
        (await db.MedicationEnrichmentJobs.AnyAsync(j => j.NormalizedName == normalizedName))
            .Should().BeFalse("Pending-строка без реальной задачи в Hangfire не должна оставаться в БД");
    }
}
