using System.Net;
using System.Net.Http.Json;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// POST /api/medical-records/{id}/summary/regenerate — пересчёт "Резюме"/"Вопросы врачу" по
/// текущим показателям записи независимо от исходной автоматической суммаризации при
/// распознавании (нужен после ручной правки показателя, см. class doc
/// ExtractionQueryService.RegenerateSummaryAsync). LM Studio недоступен через какой-либо
/// Null-переключатель (в отличие от Extraction:Enabled/Enrichment:Provider) — ILmStudioJsonClient
/// вызывается напрямую; FamilyHubWebFactory направляет LmStudio:BaseUrl на заведомо закрытый
/// loopback-порт (см. её class doc), поэтому вызов действительно быстро проваливается
/// (connection refused), а не зависит от того, поднят ли на машине реальный LM Studio.
/// </summary>
public class RegenerateSummaryApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    /// <summary>Источник — атрибут ВСЕЙ записи (заметка 1) — проставляется здесь сразу после
    /// создания, не в запросе на показатель.</summary>
    private async Task<Guid> CreateAnalysisAsync(HttpClient owner, DateOnly date, Guid? specimenId = null)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records", new CreateMedicalRecordRequest(date, null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var recordId = (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!.Id;

        if (specimenId is { } id)
        {
            var specimenResponse = await owner.PutAsJsonAsync(
                $"/api/medical-records/{recordId}/specimen", new SetRecordSpecimenRequest(id));
            specimenResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        return recordId;
    }

    private async Task<Guid> CreateIndicatorAsync(HttpClient owner, Guid recordId)
    {
        var response = await owner.PostAsJsonAsync($"/api/medical-records/{recordId}/indicators",
            new CreateIndicatorRequest("Гемоглобин", "140", "г/л", "130", "160", null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IndicatorDto>())!.Id;
    }

    [Fact]
    public async Task RegenerateSummary_WithoutSession_Returns401()
    {
        var response = await Factory.CreateClient().PostAsync($"/api/medical-records/{Guid.NewGuid()}/summary/regenerate", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegenerateSummary_UnknownRecord_Returns404()
    {
        var owner = ClientAs(FreshTelegramId());

        var response = await owner.PostAsync($"/api/medical-records/{Guid.NewGuid()}/summary/regenerate", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RegenerateSummary_RecordWithoutIndicators_Returns404()
    {
        var owner = ClientAs(FreshTelegramId());
        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));

        var response = await owner.PostAsync($"/api/medical-records/{recordId}/summary/regenerate", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "у записи ещё нет ни одного показателя — суммаризировать нечего");
    }

    [Fact]
    public async Task RegenerateSummary_NotOwner_Returns403()
    {
        var owner = ClientAs(FreshTelegramId());
        var specimenId = await SeedSpecimenAsync($"Кровь {Guid.NewGuid():N}");
        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1), specimenId);
        await CreateIndicatorAsync(owner, recordId);

        var stranger = ClientAs(FreshTelegramId());
        var response = await stranger.PostAsync($"/api/medical-records/{recordId}/summary/regenerate", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RegenerateSummary_WithIndicators_LmStudioUnavailable_ReturnsBadGateway_NotUnhandledError()
    {
        var owner = ClientAs(FreshTelegramId());
        var specimenId = await SeedSpecimenAsync($"Кровь {Guid.NewGuid():N}");
        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1), specimenId);
        await CreateIndicatorAsync(owner, recordId);

        var response = await owner.PostAsync($"/api/medical-records/{recordId}/summary/regenerate", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "LM Studio недоступен в тестовом хосте (см. class doc) — эндпоинт обязан отдать аккуратный 502, не 5xx-исключение");
    }

    private async Task<DateTime?> GetDirtyAtAsync(Guid recordId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MedicalRecords.AsNoTracking().Where(r => r.Id == recordId).Select(r => r.SummaryDirtyAt).SingleAsync();
    }

    private async Task RunJobAsync(Guid recordId, long ticks)
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RecordSummaryRegenerationJob>().RunAsync(recordId, ticks);
    }

    /// <summary>Ручная правка показателей автоматически планирует пересчёт резюме — кнопки
    /// «Пересчитать» больше нет. Пока пересчёт не выполнен (15 с дебаунса), GET /summary отдаёт
    /// 200 с pending=true, а не 404.</summary>
    [Fact]
    public async Task IndicatorEdits_MarkSummaryDirty_AndGetSummaryReportsPending()
    {
        var owner = ClientAs(FreshTelegramId());
        var specimenId = await SeedSpecimenAsync($"Кровь {Guid.NewGuid():N}");
        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1), specimenId);

        (await GetDirtyAtAsync(recordId)).Should().BeNull();
        var indicatorId = await CreateIndicatorAsync(owner, recordId);
        var afterCreate = await GetDirtyAtAsync(recordId);
        afterCreate.Should().NotBeNull("добавление показателя устаревляет резюме");

        var response = await owner.GetAsync($"/api/medical-records/{recordId}/summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RecordSummaryResponse>(JsonOpts))!.Pending.Should().BeTrue();

        // Правка и удаление — тоже (токен обновляется, дебаунс идёт от последней правки).
        await Task.Delay(20);
        (await owner.PutAsJsonAsync($"/api/indicators/{indicatorId}",
            new UpdateIndicatorRequest("Гемоглобин", "150", "г/л", "130", "160", null))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetDirtyAtAsync(recordId)).Should().BeAfter(afterCreate!.Value);
    }

    [Fact]
    public async Task SummaryJob_WithStaleToken_IsNoOp_WithCurrentToken_ClearsDirtyFlag()
    {
        var owner = ClientAs(FreshTelegramId());
        var specimenId = await SeedSpecimenAsync($"Кровь {Guid.NewGuid():N}");
        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1), specimenId);
        await CreateIndicatorAsync(owner, recordId);
        var token = (await GetDirtyAtAsync(recordId))!.Value;

        await RunJobAsync(recordId, token.Ticks - 10);
        (await GetDirtyAtAsync(recordId)).Should().Be(token, "джоба со старым токеном — устаревшая, свежая уже в пути");

        // LM Studio в тестовом хосте недоступен: пересчёт не удаётся, резюме сбрасывается, но
        // пометка снимается — фронт не должен вечно показывать «Обновляем резюме…».
        await RunJobAsync(recordId, token.Ticks);
        (await GetDirtyAtAsync(recordId)).Should().BeNull();
        (await owner.GetAsync($"/api/medical-records/{recordId}/summary")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
