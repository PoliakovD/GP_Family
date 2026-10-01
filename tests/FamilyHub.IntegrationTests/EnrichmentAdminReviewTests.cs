using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Api.Features.Admin;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Провайдер поиска, запоминающий каждый запрос — «0 вызовов до одобрения, 1 после» проверяется по нему.
/// Для препаратов отвечает сниппетом доверенного vidal.ru, для показателей — helix.ru.</summary>
public sealed class RecordingSearchProvider : IMedicationSearchProvider
{
    private readonly ConcurrentQueue<(string Query, string? Specimen)> _calls = new();

    public string Name => "FakeProvider";

    public int CallsFor(string queryFragment) => _calls.Count(c => c.Query.Contains(queryFragment, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<(string Query, string? Specimen)> Calls => _calls.ToList();

    public Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        string? specimenDisplayName = null, CancellationToken ct = default, WebSearchCallContext? callContext = null)
    {
        _calls.Enqueue((normalizedName, specimenDisplayName));
        var url = topic == WebSearchTopic.LabAnalyte ? "https://helix.ru/kb/item/test" : "https://www.vidal.ru/drugs/test";
        return Task.FromResult<IReadOnlyList<WebSnippet>>(
            [new WebSnippet("Источник", url, $"{normalizedName} — тестовое описание для интеграционного теста.")]);
    }
}

/// <summary>Управляемый ответ LM: уверенность стража и суммаризатора выбирается по токенам в названии — «низкстраж»
/// (страж 0.2), «низкрез» (суммаризатор 0.3), «безоценки» (суммаризатор без confidence). Суммаризатор узнаётся по
/// «Препарат:»/«Показатель:» в начале пользовательского текста, всё остальное — гейт легитимности.</summary>
public sealed class ReviewFakeLmClient : ILmStudioJsonClient
{
    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default, bool suppressThinking = false, bool shortTimeout = false) =>
        ExtractJsonAsync(systemPrompt, userText, ct, suppressThinking, shortTimeout);

    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, CancellationToken ct = default, bool suppressThinking = false,
        bool shortTimeout = false)
    {
        var payload = new Dictionary<string, JsonElement>();
        void Set(string key, object? value) => payload[key] = JsonSerializer.SerializeToElement(value);

        var isMedicationSummary = userText.StartsWith("Препарат:", StringComparison.Ordinal);
        var isAnalyteSummary = userText.Contains("Показатель:", StringComparison.Ordinal) && userText.Contains("[0]", StringComparison.Ordinal);
        if (!isMedicationSummary && !isAnalyteSummary)
        {
            // Гейт легитимности.
            Set("valid", true);
            Set("reason", null);
            Set("confidence", userText.Contains("низкстраж", StringComparison.OrdinalIgnoreCase) ? 0.2 : 0.95);
            Set("confidenceReason", "тест");
            return Task.FromResult(new LmStudioJsonResult(true, payload, null));
        }

        if (!userText.Contains("безоценки", StringComparison.OrdinalIgnoreCase))
        {
            Set("confidence", userText.Contains("низкрез", StringComparison.OrdinalIgnoreCase) ? 0.3 : 0.95);
            Set("confidenceReason", "тест");
        }

        Set("usedSourceIndexes", new[] { 0 });
        if (isMedicationSummary)
        {
            Set("internationalName", "Тестовое МНН");
            Set("tradeNames", new[] { "Тестпрепарат" });
            Set("form", "таблетки");
            Set("purpose", "жаропонижающее");
            // fieldSources нарочно без "form": поле заполнено, а источника у него нет — подсветка «без источника».
            Set("fieldSources", new Dictionary<string, int[]> { ["purpose"] = [0], ["internationalName"] = [0] });
        }
        else
        {
            Set("plainExplanation", "Показатель свёртывания крови.");
            Set("defaultUnit", "сек");
            Set("fieldSources", new Dictionary<string, int[]> { ["plainExplanation"] = [0] });
        }

        return Task.FromResult(new LmStudioJsonResult(true, payload, null));
    }
}

/// <summary>Форма ответа GET /api/admin/history (анонимный объект на сервере).</summary>
public sealed record KbChangeLogListDto(List<KbChangeLogItemDto> Items, int Total);

public class ReviewWebFactory : AdminWebFactory
{
    public RecordingSearchProvider Provider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddScoped<IMedicationSearchProvider>(_ => Provider);
            services.AddScoped<ILmStudioJsonClient, ReviewFakeLmClient>();
        });
    }
}

[CollectionDefinition(Name)]
public class ReviewCollection : ICollectionFixture<ReviewWebFactory>
{
    public const string Name = "EnrichmentAdminReviewIntegration";
}

/// <summary>
/// Ручное одобрение платных поисков и результатов обогащения (ADR-0018) сквозь реальный Postgres/Hangfire:
/// гейт 1 (0 вызовов провайдера до одобрения, 1 после), гейт 2 (низкая/отсутствующая уверенность не пишет kb),
/// одобрение/отклонение/правки, локи, вентиль поверх одобрения, уникальный индекс в статусах 6/7, retry из «Задач»,
/// пороги, ручные сниппеты, режим «использовать кэш», журнал с откатом, статус проверки и группы биоматериалов.
/// </summary>
[Collection(ReviewCollection.Name)]
public class EnrichmentAdminReviewTests(ReviewWebFactory factory)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Уникальное имя без цифр (иначе KbIsolationGuard принимает длинную цифровую последовательность за телефон)
    /// и без смешения алфавитов; токен задаёт поведение фейкового LM (см. ReviewFakeLmClient).</summary>
    private static string Name(string token = "обычный")
    {
        var suffix = new string(Guid.NewGuid().ToString("N").Select(c => char.IsDigit(c) ? (char)('g' + (c - '0')) : c).ToArray());
        return $"тестпрепарат {token} {suffix}";
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword })).EnsureSuccessStatusCode();
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

    /// <summary>Заводит задачу напрямую в БД и один раз запускает процессор — детерминированно, без участия Hangfire
    /// (задача не энкьюится, пока её не одобрят).</summary>
    private async Task<Guid> RunMedicationJobAsync(string normalizedName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = new MedicationEnrichmentJob
        {
            Id = Guid.NewGuid(), NormalizedName = normalizedName, SourceDisplayName = normalizedName,
            RequestedByUserId = Guid.NewGuid(), FamilyId = Guid.NewGuid(), Status = EnrichmentJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.MedicationEnrichmentJobs.Add(job);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<MedicationEnrichmentProcessor>().RunAsync(job.Id);
        return job.Id;
    }

    private async Task<MedicationEnrichmentJob> LoadJobAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .MedicationEnrichmentJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    private async Task WaitForStatusAsync(Guid id, EnrichmentJobStatus status, string because) =>
        await WaitForAsync(async () => (await LoadJobAsync(id)).Status == status, because);

    private sealed class LockedRow
    {
        public string[] LockedFields { get; set; } = [];
    }

    private async Task<GlobalMedicationKb?> KbRowAsync(string normalizedName)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .GlobalMedicationsKb.AsNoTracking().FirstOrDefaultAsync(k => k.NormalizedName == normalizedName);
    }

    // ------------------------------------------------------------------ гейт 1

    [Fact]
    public async Task PaidSearch_ParksForApproval_ZeroProviderCallsBefore_OneAfter_WithAdminEditedQuery()
    {
        var name = Name();
        var jobId = await RunMedicationJobAsync(name);

        var parked = await LoadJobAsync(jobId);
        parked.Status.Should().Be(EnrichmentJobStatus.AwaitingSearchApproval);
        parked.QueryConfidence.Should().Be(0.95, "уверенность стража сохраняется для очереди");
        parked.ProposedQueryText.Should().Be(name, "предложенный текст запроса — нормализованное имя");
        factory.Provider.CallsFor(name).Should().Be(0, "до одобрения платный провайдер не вызывается");

        var admin = await AdminClientAsync();
        var edited = name + " инструкция";
        var response = await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { queryText = edited });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await WaitForStatusAsync(jobId, EnrichmentJobStatus.Completed, "после одобрения задача должна дойти до записи в справочник");
        factory.Provider.CallsFor(edited).Should().Be(1, "ровно один платный вызов — с правленым текстом запроса");
        factory.Provider.CallsFor(name).Should().Be(1);

        var kb = await KbRowAsync(name);
        kb.Should().NotBeNull();
        kb!.VerificationStatus.Should().Be(KbVerificationStatus.AiUnverified, "автообогащение без человека — «не проверено»");

        var done = await LoadJobAsync(jobId);
        done.SearchApprovedAt.Should().NotBeNull();
        done.ResultConfidence.Should().Be(0.95);
    }

    [Fact]
    public async Task CacheHit_PassesWithoutApproval_AndWithoutProviderCall()
    {
        var name = Name();
        var first = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();
        (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{first}/approve", new { })).EnsureSuccessStatusCode();
        await WaitForStatusAsync(first, EnrichmentJobStatus.Completed, "первая задача");
        factory.Provider.CallsFor(name).Should().Be(1);

        // Вторая задача по тому же имени, когда kb уже заполнен, завершается сразу (kb-hit); чтобы проверить именно кэш,
        // удаляем запись справочника: кэш поиска остаётся свежим.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM kb.global_medications_kb WHERE "NormalizedName" = {name}""");
        }

        var second = await RunMedicationJobAsync(name);
        (await LoadJobAsync(second)).Status.Should().Be(EnrichmentJobStatus.Completed,
            "кэш-хит бесплатен и проходит сразу — одобрения не требует");
        factory.Provider.CallsFor(name).Should().Be(1, "второй платный вызов не понадобился");
    }

    [Fact]
    public async Task ClosedValve_ApprovedJob_GoesDeferred_AndResumesWithoutNewApproval()
    {
        var name = Name();
        var jobId = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();

        (await admin.PutAsJsonAsync("/api/admin/enrichment/web-search", new { isPaused = true, note = "тест" })).EnsureSuccessStatusCode();
        try
        {
            (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { })).EnsureSuccessStatusCode();
            await WaitForStatusAsync(jobId, EnrichmentJobStatus.Deferred, "вентиль действует поверх одобрения");
            factory.Provider.CallsFor(name).Should().Be(0);
            (await LoadJobAsync(jobId)).SearchApprovedAt.Should().NotBeNull("одобрение сохраняется — повторно не спросим");
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/admin/enrichment/web-search", new { isPaused = false, note = (string?)null })).EnsureSuccessStatusCode();
        }

        await WaitForStatusAsync(jobId, EnrichmentJobStatus.Completed, "после открытия вентиля отложенная задача возобновляется сама");
        factory.Provider.CallsFor(name).Should().Be(1);
    }

    [Fact]
    public async Task RetryFromJobsList_DoesNotRestartJobsAwaitingAdmin()
    {
        var name = Name();
        var jobId = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();

        var response = await admin.PostAsync($"/api/admin/pipeline/jobs/{jobId}/retry?type=medication", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "задачи на одобрении перезапускаются только из очереди «Одобрение»");
        (await LoadJobAsync(jobId)).Status.Should().Be(EnrichmentJobStatus.AwaitingSearchApproval);
        factory.Provider.CallsFor(name).Should().Be(0);
    }

    [Fact]
    public async Task DeferredReleaseAndRecoverySweep_DoNotTouchJobsAwaitingAdmin()
    {
        var name = Name();
        var jobId = await RunMedicationJobAsync(name);

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DeferredEnrichmentReleaseJob>().RunAsync();
        await scope.ServiceProvider.GetRequiredService<LmStudioRecoverySweepJob>().RunAsync();

        (await LoadJobAsync(jobId)).Status.Should().Be(EnrichmentJobStatus.AwaitingSearchApproval,
            "ни снятие вентиля, ни sweep не возобновляют задачу в обход одобрения");
        factory.Provider.CallsFor(name).Should().Be(0);
    }

    [Theory]
    [InlineData(EnrichmentJobStatus.AwaitingSearchApproval)]
    [InlineData(EnrichmentJobStatus.AwaitingResultReview)]
    public async Task UniqueIndex_PreventsDuplicateJobs_InAwaitingStatuses(EnrichmentJobStatus status)
    {
        var name = Name();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        MedicationEnrichmentJob New() => new()
        {
            Id = Guid.NewGuid(), NormalizedName = name, SourceDisplayName = name, RequestedByUserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(), Status = status, CreatedAt = DateTime.UtcNow,
        };

        db.MedicationEnrichmentJobs.Add(New());
        await db.SaveChangesAsync();
        db.MedicationEnrichmentJobs.Add(New());
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("статусы 6/7 входят в частичный уникальный индекс — иначе дубль в очереди");
    }

    [Fact]
    public async Task VisitMedication_ParksForApproval_UsingGuardConfidence_AndRunsAfterApproval()
    {
        var name = Name("низкстраж");
        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = new VisitMedicationEnrichmentJob
            {
                Id = Guid.NewGuid(), NormalizedName = name, SourceDisplayName = name, RequestedByUserId = Guid.NewGuid(),
                Status = EnrichmentJobStatus.Pending, CreatedAt = DateTime.UtcNow,
            };
            db.VisitMedicationEnrichmentJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            // У этого конвейера нет собственного шага легитимности — оценка этапа запроса снимается именно на гейте.
            await scope.ServiceProvider.GetRequiredService<VisitMedicationEnrichmentProcessor>().RunAsync(jobId);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var parked = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .VisitMedicationEnrichmentJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            parked.Status.Should().Be(EnrichmentJobStatus.AwaitingSearchApproval);
            parked.QueryConfidence.Should().Be(0.2);
        }

        factory.Provider.CallsFor(name).Should().Be(0);
        var admin = await AdminClientAsync();
        var inbox = await admin.GetFromJsonAsync<ReviewInboxResponse>("/api/admin/review/inbox?stage=search&kind=visit-medication", JsonOpts);
        inbox!.Rows.Single(r => r.Id == jobId).BelowThreshold.Should().BeTrue();

        (await admin.PostAsJsonAsync($"/api/admin/review/searches/visit-medication/{jobId}/approve", new { })).EnsureSuccessStatusCode();
        await WaitForAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var status = (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .VisitMedicationEnrichmentJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status;
            return status == EnrichmentJobStatus.Completed;
        }, "после одобрения задача заключения врача доходит до записи");
        factory.Provider.CallsFor(name).Should().Be(1);
    }

    // ------------------------------------------------------------------ гейт 2

    [Fact]
    public async Task LowResultConfidence_DoesNotWriteKb_UntilAdminApproves_ThenVerifiedAndJournaled()
    {
        var name = Name("низкрез");
        var jobId = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();
        (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { })).EnsureSuccessStatusCode();
        await WaitForStatusAsync(jobId, EnrichmentJobStatus.AwaitingResultReview, "уверенность 0.3 ниже порога 0.8 — результат на ревью");

        var review = await LoadJobAsync(jobId);
        review.DraftPayloadJson.Should().NotBeNullOrEmpty();
        review.ResultConfidence.Should().Be(0.3);
        (await KbRowAsync(name)).Should().BeNull("ниже порога — в справочник не пишем");

        // Очередь видит задачу: ниже порога, с черновиком, атрибуцией полей и сниппетами.
        var inbox = await admin.GetFromJsonAsync<ReviewInboxResponse>("/api/admin/review/inbox?stage=result", JsonOpts);
        inbox!.Rows.Should().Contain(r => r.Id == jobId && r.BelowThreshold);
        var detail = await admin.GetFromJsonAsync<ReviewItemDetailDto>($"/api/admin/review/results/medication/{jobId}", JsonOpts);
        detail!.Draft.Should().NotBeNull();
        detail.Current.Should().BeNull();
        detail.FieldSourceInfo!.Available.Should().BeTrue();
        detail.FieldSourceInfo.FieldsWithoutSource.Should().Contain("form", "поле «форма» заполнено, а модель не указала для него источник");
        detail.Sources.Should().ContainSingle(s => s.UsedInDraft && s.Domain == "www.vidal.ru");

        (await admin.PostAsJsonAsync($"/api/admin/review/results/medication/{jobId}/approve", new { note = "проверено" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await LoadJobAsync(jobId)).Status.Should().Be(EnrichmentJobStatus.Completed);
        var kb = await KbRowAsync(name);
        kb.Should().NotBeNull();
        kb!.VerificationStatus.Should().Be(KbVerificationStatus.AdminVerified, "одобрено без правок — проверено человеком");

        // Журнал: запись ИИ («admin» — одобрение из очереди) и отметка проверки; откат отметки возвращает «не проверено».
        var history = await admin.GetFromJsonAsync<KbChangeLogListDto>(
            $"/api/admin/history?target=MedicationKb&targetId={kb.Id}", JsonOpts);
        history!.Items.Select(i => i.Action).Should().Contain(["ai-write", "verify"]);
        var verify = history.Items.First(i => i.Action == "verify");
        verify.CanRevert.Should().BeTrue();
        (await admin.PostAsync($"/api/admin/history/{verify.Id}/revert", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await KbRowAsync(name))!.VerificationStatus.Should().Be(KbVerificationStatus.AiUnverified);
        (await admin.PostAsync($"/api/admin/history/{verify.Id}/revert", null)).StatusCode.Should().Be(HttpStatusCode.Conflict,
            "одну запись журнала откатываем один раз");
    }

    [Fact]
    public async Task MissingConfidence_IsTreatedAsBelowThreshold()
    {
        var name = Name("безоценки");
        var jobId = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();
        (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { })).EnsureSuccessStatusCode();
        await WaitForStatusAsync(jobId, EnrichmentJobStatus.AwaitingResultReview, "null-уверенность — безопасный дефолт «ниже порога»");

        (await LoadJobAsync(jobId)).ResultConfidence.Should().BeNull();
        (await KbRowAsync(name)).Should().BeNull();
    }

    [Fact]
    public async Task RejectResult_FailsJobWithRejectedByAdmin_AndWritesNothing()
    {
        var name = Name("низкрез");
        var jobId = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();
        (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { })).EnsureSuccessStatusCode();
        await WaitForStatusAsync(jobId, EnrichmentJobStatus.AwaitingResultReview, "ожидание ревью");

        (await admin.PostAsJsonAsync($"/api/admin/review/results/medication/{jobId}/reject", new { reason = "сомнительно" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var job = await LoadJobAsync(jobId);
        job.Status.Should().Be(EnrichmentJobStatus.Failed);
        job.FailureReason.Should().Be(EnrichmentFailureReason.RejectedByAdmin);
        job.Error.Should().Be("сомнительно");
        (await KbRowAsync(name)).Should().BeNull();

        (await admin.PostAsJsonAsync($"/api/admin/review/results/medication/{jobId}/approve", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "повторное действие над уже обработанной задачей — 409");
    }

    [Fact]
    public async Task ApproveWithEdits_LocksEditedFields_AndMarksAdminEdited()
    {
        var name = Name("низкрез");
        var jobId = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();
        (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { })).EnsureSuccessStatusCode();
        await WaitForStatusAsync(jobId, EnrichmentJobStatus.AwaitingResultReview, "ожидание ревью");

        var detail = await admin.GetFromJsonAsync<ReviewItemDetailDto>($"/api/admin/review/results/medication/{jobId}", JsonOpts);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(detail!.Draft!.PayloadJson)!.AsObject();
        payload["purpose"] = "правка администратора";

        var response = await admin.PostAsJsonAsync($"/api/admin/review/results/medication/{jobId}/approve", new
        {
            payloadJson = payload.ToJsonString(), displayName = "Правленое название",
        });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var kb = await KbRowAsync(name);
        kb!.DisplayName.Should().Be("Правленое название");
        kb.PayloadJson.Should().Contain("правка администратора");
        kb.VerificationStatus.Should().Be(KbVerificationStatus.AdminEdited);

        using var scope = factory.Services.CreateScope();
        var locks = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<LockedRow>($"""SELECT "LockedFields" FROM kb.global_medications_kb WHERE "NormalizedName" = {name}""").SingleAsync();
        locks.LockedFields.Should().Contain(["displayName", "payload.purpose"], "правленые админом поля попадают в LockedFields");
        locks.LockedFields.Should().NotContain("payload", "лочатся отдельные поля, а не весь payload");
    }

    // ------------------------------------------------------------------ очередь и пороги

    [Fact]
    public async Task Inbox_ListsBelowThresholdFirst()
    {
        var normal = Name();
        var suspicious = Name("низкстраж");
        var normalJob = await RunMedicationJobAsync(normal);
        var suspiciousJob = await RunMedicationJobAsync(suspicious);
        var admin = await AdminClientAsync();

        var inbox = await admin.GetFromJsonAsync<ReviewInboxResponse>("/api/admin/review/inbox?stage=search", JsonOpts);
        var ids = inbox!.Rows.Select(r => r.Id).ToList();
        ids.IndexOf(suspiciousJob).Should().BeGreaterThanOrEqualTo(0);
        ids.IndexOf(normalJob).Should().BeGreaterThanOrEqualTo(0);
        ids.IndexOf(suspiciousJob).Should().BeLessThan(ids.IndexOf(normalJob), "подозрительные (ниже порога) — выше обычных");
        inbox.Rows.Single(r => r.Id == suspiciousJob).BelowThreshold.Should().BeTrue();
        inbox.Rows.Single(r => r.Id == normalJob).BelowThreshold.Should().BeFalse();

        // Счётчик очереди попадает и в «Требует внимания» (бейдж в меню).
        var attention = await admin.GetFromJsonAsync<AdminAttentionDto>("/api/admin/pipeline/attention", JsonOpts);
        attention!.ReviewQueue.Searches.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ReviewConfig_RoundTrips_AndRejectsOutOfRangeValues()
    {
        var admin = await AdminClientAsync();
        var original = await admin.GetFromJsonAsync<EnrichmentReviewConfigDto>("/api/admin/review/config", JsonOpts);

        (await admin.PutAsJsonAsync("/api/admin/review/config", new
        {
            medicationQueryMinConfidence = 1.5, analyteQueryMinConfidence = 0.7,
            medicationResultMinConfidence = 0.8, analyteResultMinConfidence = 0.8,
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "порог вне 0..1 — 400");

        try
        {
            var response = await admin.PutAsJsonAsync("/api/admin/review/config", new
            {
                medicationQueryMinConfidence = 0.5, analyteQueryMinConfidence = 0.6,
                medicationResultMinConfidence = 0.1, analyteResultMinConfidence = 0.95,
            });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var saved = await admin.GetFromJsonAsync<EnrichmentReviewConfigDto>("/api/admin/review/config", JsonOpts);
            saved!.MedicationQueryMinConfidence.Should().Be(0.5);
            saved.AnalyteResultMinConfidence.Should().Be(0.95);

            // Порог действует на следующей же задаче: при пороге результата 0.1 уверенность 0.3 уже достаточна.
            var name = Name("низкрез");
            var jobId = await RunMedicationJobAsync(name);
            (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { })).EnsureSuccessStatusCode();
            await WaitForStatusAsync(jobId, EnrichmentJobStatus.Completed, "порог 0.1 пропускает уверенность 0.3 без ревью");
            (await KbRowAsync(name)).Should().NotBeNull();
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/review/config", new
            {
                medicationQueryMinConfidence = original!.MedicationQueryMinConfidence, analyteQueryMinConfidence = original.AnalyteQueryMinConfidence,
                medicationResultMinConfidence = original.MedicationResultMinConfidence, analyteResultMinConfidence = original.AnalyteResultMinConfidence,
            });
        }
    }

    // ------------------------------------------------------------------ источники

    [Fact]
    public async Task ManualSnippet_SurvivesCacheRefresh_AndExpertKnowledgeCanReplacePaidSearch()
    {
        var name = Name();
        var jobId = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();

        // Кэша ещё нет — «ensure» создаёт пустую строку, в неё добавляем знание эксперта.
        var ensure = await admin.PostAsync($"/api/admin/review/items/medication/{jobId}/cache/ensure", null);
        ensure.EnsureSuccessStatusCode();
        var cacheId = (await ensure.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(JsonOpts))!["cacheId"];

        var tooLong = await admin.PostAsJsonAsync($"/api/admin/review/cache/medication/{cacheId}/snippets",
            new { kind = "expert-knowledge", text = new string('а', 801) });
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest, "целые статьи код не принимает — лимит 800 символов");

        var add = await admin.PostAsJsonAsync($"/api/admin/review/cache/medication/{cacheId}/snippets",
            new { kind = "expert-knowledge", text = "Знание эксперта: показания и дозировка.", note = "из практики" });
        add.StatusCode.Should().Be(HttpStatusCode.OK);

        // Режим «использовать кэш» — платного поиска не будет, задача строится на знании эксперта.
        (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { mode = "use-cache" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        await WaitForStatusAsync(jobId, EnrichmentJobStatus.Completed, "задача завершается на ручном сниппете");
        factory.Provider.CallsFor(name).Should().Be(0, "use-cache — ни одного платного вызова");

        var kb = await KbRowAsync(name);
        kb!.Source.Should().Contain("Эксперт: админ");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = await db.MedicationSearchCaches.AsNoTracking().SingleAsync(c => c.Id == cacheId);
        cache.SnippetsJson.Should().Contain("expert-knowledge").And.Contain("Manual");

        // Свежий платный поиск поверх строки не стирает ручной сниппет.
        await scope.ServiceProvider.GetRequiredService<MedicationSearchCacheService>().RecordSearchAsync(
            name, "FakeProvider", [new WebSnippet("Новый", "https://www.vidal.ru/new", "новый текст")]);
        var after = await db.MedicationSearchCaches.AsNoTracking().SingleAsync(c => c.Id == cacheId);
        after.SnippetsJson.Should().Contain("expert-knowledge", "ручной сниппет переживает автообновление кэша").And.Contain("vidal.ru/new");

        // Журнал кэша записал добавление, откат возвращает набор.
        var history = await admin.GetFromJsonAsync<KbChangeLogListDto>(
            $"/api/admin/history?target=MedicationSearchCache&targetId={cacheId}", JsonOpts);
        history!.Items.Select(i => i.Action).Should().Contain("cache-add");
    }

    // ------------------------------------------------------------------ статус проверки kb

    [Fact]
    public async Task AutoEnrichment_ResetsVerification_OnlyWhenPayloadChanges()
    {
        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<KbWriter>();
        var catalog = scope.ServiceProvider.GetRequiredService<AdminCatalogService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = Name();

        MedicationSummary Summary(string purpose) => new(
            "Тестовое МНН", ["Тестпрепарат"], "таблетки", purpose, null, null, null, null, null, [0]);

        var first = await writer.UpsertAsync(name, name, Summary("жаропонижающее"), "тест");
        first.Success.Should().BeTrue();
        (await db.GlobalMedicationsKb.AsNoTracking().SingleAsync(k => k.Id == first.KbId!.Value))
            .VerificationStatus.Should().Be(KbVerificationStatus.AiUnverified);

        (await catalog.MarkVerifiedAsync(KbChangeTarget.MedicationKb, first.KbId!.Value)).Should().BeTrue();
        (await db.GlobalMedicationsKb.AsNoTracking().SingleAsync(k => k.Id == first.KbId.Value))
            .VerificationStatus.Should().Be(KbVerificationStatus.AdminVerified);

        // Тот же payload — проверка остаётся.
        await writer.UpsertAsync(name, name, Summary("жаропонижающее"), "тест");
        (await db.GlobalMedicationsKb.AsNoTracking().SingleAsync(k => k.Id == first.KbId.Value))
            .VerificationStatus.Should().Be(KbVerificationStatus.AdminVerified, "payload не изменился");

        // Изменённый payload — сброс в «не проверено».
        await writer.UpsertAsync(name, name, Summary("обезболивающее"), "тест");
        var row = await db.GlobalMedicationsKb.AsNoTracking().SingleAsync(k => k.Id == first.KbId.Value);
        row.VerificationStatus.Should().Be(KbVerificationStatus.AiUnverified, "автообогащение изменило payload");
        row.VerifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task UserFacingKbResponses_NeverExposeVerificationFields()
    {
        var name = Name();
        var jobId = await RunMedicationJobAsync(name);
        var admin = await AdminClientAsync();
        (await admin.PostAsJsonAsync($"/api/admin/review/searches/medication/{jobId}/approve", new { })).EnsureSuccessStatusCode();
        await WaitForStatusAsync(jobId, EnrichmentJobStatus.Completed, "запись справочника создана");
        var kb = (await KbRowAsync(name))!;
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminCatalogService>().MarkVerifiedAsync(KbChangeTarget.MedicationKb, kb.Id);

        // Обычный пользователь (не админ): каталог справочника — тот же kb, статус проверки в ответе отсутствовать обязан.
        var user = factory.CreateClientAs(Random.Shared.NextInt64(1_000_000_000, 9_000_000_000));
        var consent = await user.GetFromJsonAsync<Dictionary<string, JsonElement>>("/api/consents/current");
        (await user.PostAsJsonAsync("/api/consents/accept", new { version = consent!["version"].GetString() })).EnsureSuccessStatusCode();
        var card = await user.GetStringAsync($"/api/kb/medications/{kb.Id}");
        card.Should().Contain(kb.DisplayName);
        card.ToLowerInvariant().Should().NotContain("verif", "статус проверки — внутренний маркер админки");
        var list = await user.GetStringAsync("/api/kb/medications/?skip=0&take=20&q=" + Uri.EscapeDataString(name));
        list.ToLowerInvariant().Should().NotContain("verif");

        // А админская деталь его содержит.
        var adminDetail = await admin.GetStringAsync($"/api/admin/kb/medications/{kb.Id}");
        adminDetail.Should().Contain("verificationStatus");
    }

    // ------------------------------------------------------------------ группы поиска биоматериалов

    private async Task<Guid> CreateSpecimenAsync(string display)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.GlobalSpecimensKb.Add(new GlobalSpecimenKb
        {
            Id = id, NormalizedName = display.ToLowerInvariant() + Guid.NewGuid().ToString("N")[..6], DisplayName = display,
            Source = "тест", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task SpecimenSearchGroup_MergesExistingCacheRows_AndSharesOneSearchWithinGroup()
    {
        var blood = await CreateSpecimenAsync("Кровь");
        var venous = await CreateSpecimenAsync("Венозная кровь");
        var urine = await CreateSpecimenAsync("Моча");
        var analyte = "ачтв" + new string(Guid.NewGuid().ToString("N")[..6].Select(c => char.IsDigit(c) ? (char)('g' + (c - '0')) : c).ToArray());
        var admin = await AdminClientAsync();

        using (var scope = factory.Services.CreateScope())
        {
            // До групп АЧТВ × (кровь, венозная кровь) = два независимых платных поиска и две строки кэша.
            var cache = scope.ServiceProvider.GetRequiredService<LabAnalyteSearchCacheService>();
            await cache.RecordSearchAsync(analyte, blood, "FakeProvider", [new WebSnippet("A", "https://helix.ru/a", "текст a")]);
            await Task.Delay(50);
            await cache.RecordSearchAsync(analyte, venous, "FakeProvider", [new WebSnippet("B", "https://helix.ru/b", "текст b")]);
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().LabAnalyteSearchCaches.CountAsync(c => c.NormalizedName == analyte))
                .Should().Be(2);
        }

        (await admin.PutAsJsonAsync($"/api/admin/kb/specimens/{blood}/search-group", new { searchGroupKey = "Кровь" })).EnsureSuccessStatusCode();
        var merged = await admin.PutAsJsonAsync($"/api/admin/kb/specimens/{venous}/search-group", new { searchGroupKey = "кровь" });
        merged.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cache = scope.ServiceProvider.GetRequiredService<LabAnalyteSearchCacheService>();

            var rows = await db.LabAnalyteSearchCaches.AsNoTracking().Where(c => c.NormalizedName == analyte).ToListAsync();
            rows.Should().ContainSingle("строки кэша внутри группы схлопнуты в одну (свежая побеждает)");
            rows[0].SearchGroupKey.Should().Be("group:кровь");

            // Запрос у обоих биоматериалов один: слово группы вместо названия биоматериала.
            (await cache.GetSearchGroupAsync(blood)).QueryLabel.Should().Be("кровь");
            (await cache.GetSearchGroupAsync(venous)).QueryLabel.Should().Be("кровь");
            (await cache.GetCachedAsync(analyte, blood))!.Snippets.Should().BeEquivalentTo(
                (await cache.GetCachedAsync(analyte, venous))!.Snippets, "одна строка кэша на оба биоматериала");

            // «Двойник» для другой группы (моча): свежий кэш крови предлагается как бесплатная альтернатива.
            var twins = await cache.FindTwinsAsync(analyte, urine);
            twins.Should().ContainSingle().Which.SearchGroupKey.Should().Be("кровь");
            (await cache.FindTwinsAsync(analyte, venous)).Should().BeEmpty("внутри одной группы двойников нет — кэш общий");
        }

        // Выход биоматериала из группы: ему остаётся копия кэша.
        var leave = await admin.PutAsJsonAsync($"/api/admin/kb/specimens/{venous}/search-group", new { searchGroupKey = (string?)null });
        leave.EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.LabAnalyteSearchCaches.CountAsync(c => c.NormalizedName == analyte)).Should().Be(2,
                "у вышедшего биоматериала своя копия строки группы");
        }
    }

    [Fact]
    public async Task LabAnalyte_ParksForApproval_OffersTwinCache_AndUseCacheSkipsPaidSearch()
    {
        var blood = await CreateSpecimenAsync("Кровь");
        var urine = await CreateSpecimenAsync("Моча");
        var analyte = "белок" + new string(Guid.NewGuid().ToString("N")[..8].Select(c => char.IsDigit(c) ? (char)('g' + (c - '0')) : c).ToArray());
        var admin = await AdminClientAsync();

        // Свежий кэш у «двойника» (кровь) — ранее оплаченный поиск.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LabAnalyteSearchCacheService>().RecordSearchAsync(
                analyte, blood, "FakeProvider",
                [new WebSnippet("Хеликс", "https://helix.ru/kb/item/test", $"{analyte} — описание показателя в крови.")]);
        }

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = new LabAnalyteEnrichmentJob
            {
                Id = Guid.NewGuid(), NormalizedName = analyte, SpecimenKbId = urine, SourceDisplayName = analyte,
                RequestedByUserId = Guid.NewGuid(), Origin = EnrichmentRequestOrigin.Extraction,
                Status = EnrichmentJobStatus.Pending, CreatedAt = DateTime.UtcNow,
            };
            db.LabAnalyteEnrichmentJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            await scope.ServiceProvider.GetRequiredService<LabAnalyteEnrichmentProcessor>().RunAsync(jobId);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.LabAnalyteEnrichmentJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status
                .Should().Be(EnrichmentJobStatus.AwaitingSearchApproval, "другая группа биоматериала — свой кэш-промах, платный поиск ждёт одобрения");
        }

        factory.Provider.CallsFor(analyte).Should().Be(0);

        var detail = await admin.GetFromJsonAsync<ReviewItemDetailDto>($"/api/admin/review/items/lab-analyte/{jobId}", JsonOpts);
        detail!.Twins.Should().ContainSingle(t => t.Specimen == "Кровь", "свежий кэш того же показателя у другого биоматериала предлагается как бесплатная альтернатива");
        var inbox = await admin.GetFromJsonAsync<ReviewInboxResponse>("/api/admin/review/inbox?stage=search&kind=lab-analyte", JsonOpts);
        inbox!.Rows.Single(r => r.Id == jobId).HasTwins.Should().BeTrue();

        var approve = await admin.PostAsJsonAsync($"/api/admin/review/searches/lab-analyte/{jobId}/approve",
            new { mode = "use-cache", twinCacheId = detail.Twins[0].CacheId });
        approve.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await WaitForAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (await db.LabAnalyteEnrichmentJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status == EnrichmentJobStatus.Completed;
        }, "кэш двойника принят — задача завершается без платного поиска");
        factory.Provider.CallsFor(analyte).Should().Be(0, "платный поиск не выполнялся");
    }
}
