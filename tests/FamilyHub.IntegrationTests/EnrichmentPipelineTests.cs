using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Веб-поиск, отвечающий одним доверенным сниппетом независимо от запроса — детерминизм
/// без реального Brave API/сети (см. ADR-0005: провайдер подключается за интерфейсом именно
/// затем, чтобы конвейер можно было проверить без реального внешнего вызова).</summary>
file sealed class FakeMedicationSearchProvider : IMedicationSearchProvider
{
    public string Name => "FakeProvider";

    public Task<IReadOnlyList<WebSnippet>> SearchAsync(string normalizedName, CancellationToken ct = default) =>
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

        var medicationId = await CreateMedicationAsync(admin, medkitId, $"Тестовыйпрепарат{Guid.NewGuid():N}");

        await WaitForAsync(
            async () => (await GetKbStatusAsync(admin, medicationId)).Status == StatusReady,
            "фоновый конвейер (Hangfire, очередь enrichment) должен довести задачу до Completed");

        var status = await GetKbStatusAsync(admin, medicationId);
        status.Card!.Source.Should().Contain("FakeProvider");

        (await admin.PostAsync("/dev/trigger-outbox-dispatch", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        await WaitForAsync(async () =>
        {
            var notifications = await (await admin.GetAsync("/api/notifications"))
                .Content.ReadFromJsonAsync<List<NotificationItemDto>>(JsonOpts);
            return notifications!.Any(n => n.Type == NotificationType.MedicationEnriched);
        }, "пользователь, сохранивший медикамент, должен получить уведомление о пополнении справочника");
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

        var sharedName = $"Одинаковыйпрепарат{Guid.NewGuid():N}";

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
