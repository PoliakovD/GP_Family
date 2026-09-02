using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Пересборка справочника показателей поверх исправленной чистки имён/резолвинга источника
/// (пересборка enrich-пайплайна, §4.2 плана) — реальный Postgres нужен по той же причине, что и
/// остальным тестам этого конвейера: raw SQL (DELETE FROM kb.global_lab_analytes_kb) недоступен
/// на SQLite. Admin:Enabled=true (AdminWebFactory) — эндпоинты старта/статуса за PlatformAdmin.
/// Данные "грязного" состояния заводятся напрямую через AppDbContext (не через HTTP-конвейер
/// извлечения) — тест бьёт по логике самой пересборки, не по распознаванию документа.
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class LabAnalyteKbRebuildJobTests(AdminWebFactory factory)
{
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    private async Task<Guid> SeedSpecimenAsync(AppDbContext db, string displayName)
    {
        var id = Guid.NewGuid();
        db.GlobalSpecimensKb.Add(new GlobalSpecimenKb
        {
            Id = id,
            NormalizedName = LabAnalyteNormalizer.Normalize(displayName) + Guid.NewGuid().ToString("N")[..6],
            DisplayName = displayName,
            Source = "тест",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
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
    public async Task Rebuild_MergesDuplicateIndicators_ClearsCatalog_RekeysCache_ReseedsOnlyResolvedSpecimens()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var bloodId = await SeedSpecimenAsync(db, "Кровь");
        var recordId = Guid.NewGuid();
        db.MedicalRecords.Add(new MedicalRecord
        {
            Id = recordId, OwnerUserId = OwnerUserId, Kind = MedicalRecordKind.Analysis,
            RecordDate = new DateOnly(2026, 1, 1), ExtractionStatus = ExtractionStatus.Ready, CreatedAt = DateTime.UtcNow,
        });

        // Пара показателей ОДНОЙ записи, которые до пересборки различались по "грязному" ключу
        // (нумерация пункта бланка) — после пересчёта AnalyteKey должны схлопнуться в один.
        // "Дублирующий" вариант оставлен без RawDisplayName (имитирует запись, распознанную ДО
        // этой пересборки) — как раз тот случай, который RecalculateIndicatorsAsync должен вылечить.
        var winnerId = Guid.NewGuid();
        var loserId = Guid.NewGuid();
        var staleKbIdOnIndicator = Guid.NewGuid();
        db.LabIndicators.AddRange(
            new LabIndicator
            {
                Id = winnerId, MedicalRecordId = recordId, RecordDate = new DateOnly(2026, 1, 1), OwnerUserId = OwnerUserId,
                AnalyteKey = "1 гемоглобин", DisplayName = "1. ГЕМОГЛОБИН", SpecimenKbId = bloodId, Position = 0,
                ValueRaw = "118", Flag = IndicatorFlag.Normal, CreatedAt = DateTime.UtcNow,
                // Привязка к (ещё не заведённой) старой строке справочника — этап очистки должен
                // её сбросить вместе с RefSource, иначе показатель указывал бы на удалённую строку.
                KbAnalyteId = staleKbIdOnIndicator, RefSource = RefSource.KbFixed,
            },
            new LabIndicator
            {
                Id = loserId, MedicalRecordId = recordId, RecordDate = new DateOnly(2026, 1, 1), OwnerUserId = OwnerUserId,
                AnalyteKey = "гемоглобин", DisplayName = "Гемоглобин", SpecimenKbId = bloodId, Position = 1,
                ValueRaw = "", Flag = IndicatorFlag.Unknown, CreatedAt = DateTime.UtcNow,
            });

        // Показатель с нерезолвленным источником — пересев НЕ должен поставить по нему задачу
        // обогащения (жёсткое требование, гейт в LabAnalyteEnrichmentRequestService).
        db.LabIndicators.Add(new LabIndicator
        {
            Id = Guid.NewGuid(), MedicalRecordId = recordId, RecordDate = new DateOnly(2026, 1, 1), OwnerUserId = OwnerUserId,
            AnalyteKey = "мутноепятно", DisplayName = "Мутное пятно", SpecimenKbId = SpecimenContextIds.Unresolved, Position = 2,
            ValueRaw = "да", Flag = IndicatorFlag.Unknown, CreatedAt = DateTime.UtcNow,
        });

        // Строка справочника, которая должна быть удалена на этапе очистки.
        var staleKbId = Guid.NewGuid();
        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = staleKbId, NormalizedName = "гемоглобин", SpecimenKbId = bloodId, DisplayName = "Гемоглобин",
            PayloadJson = "{}", Source = "тест", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        // Кэш сниппетов с "грязным" NormalizedName — должен перенормализоваться, не пропасть.
        db.LabAnalyteSearchCaches.Add(new LabAnalyteSearchCache
        {
            Id = Guid.NewGuid(), NormalizedName = "1 гемоглобин", SpecimenKbId = bloodId, Provider = "тест",
            LastUpdatedAt = DateTime.UtcNow, CanBeUpdatedAfter = DateTime.UtcNow.AddMonths(1),
            SnippetsJson = "[]",
        });

        await db.SaveChangesAsync();

        var admin = await AdminClientAsync();
        var startResponse = await admin.PostAsync("/api/admin/kb/lab-analytes/rebuild", null);
        startResponse.EnsureSuccessStatusCode();

        await WaitForAsync(async () =>
        {
            var status = await admin.GetFromJsonAsync<RebuildStatusDto>("/api/admin/kb/lab-analytes/rebuild/status");
            return status!.Status is "Completed" or "Failed";
        }, "пересборка должна завершиться (Hangfire, очередь enrichment)");

        var finalStatus = await admin.GetFromJsonAsync<RebuildStatusDto>("/api/admin/kb/lab-analytes/rebuild/status");
        finalStatus!.Status.Should().Be("Completed", finalStatus.LastError);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Дубликат схлопнулся — победил показатель с непустым ValueRaw (winnerId), проигравший удалён.
        var remaining = await verifyDb.LabIndicators.Where(i => i.MedicalRecordId == recordId).ToListAsync();
        remaining.Should().HaveCount(2, "два исходных дубликата схлопнулись в один + показатель с нерезолвленным источником остался как есть");
        var mergedIndicator = remaining.Should().ContainSingle(i => i.SpecimenKbId == bloodId).Which;
        mergedIndicator.Id.Should().Be(winnerId, "непустой ValueRaw должен победить при слиянии");
        mergedIndicator.AnalyteKey.Should().Be("гемоглобин");
        mergedIndicator.DisplayName.Should().Be("Гемоглобин", "КАПС снят, нумерация снята");
        mergedIndicator.KbAnalyteId.Should().BeNull("справочник очищен, старая ссылка сброшена");
        mergedIndicator.RefSource.Should().Be(RefSource.None, "KbFixed сброшен вместе со ссылкой на удалённую строку справочника");

        // Справочник очищен.
        (await verifyDb.GlobalLabAnalytesKb.AnyAsync(k => k.Id == staleKbId)).Should().BeFalse();

        // Кэш перенормализован.
        var cache = await verifyDb.LabAnalyteSearchCaches.SingleAsync(c => c.SpecimenKbId == bloodId);
        cache.NormalizedName.Should().Be("гемоглобин");

        // Пересев — задача поставлена ТОЛЬКО для резолвленного источника (гемоглобин/кровь), не
        // для показателя с SpecimenKbId=Unresolved ("мутное пятно").
        var jobs = await verifyDb.LabAnalyteEnrichmentJobs.Where(j => j.NormalizedName == "гемоглобин" && j.SpecimenKbId == bloodId).ToListAsync();
        jobs.Should().ContainSingle();
        jobs[0].Force.Should().BeTrue();

        (await verifyDb.LabAnalyteEnrichmentJobs.AnyAsync(j => j.SpecimenKbId == SpecimenContextIds.Unresolved))
            .Should().BeFalse("жёсткое требование — обогащение никогда не ставится в очередь для нерезолвленного источника");
    }

    private record RebuildStatusDto(Guid? RunId, string? Status, DateTime? StartedAt, DateTime? FinishedAt, string? LastError,
        int StageIndex, int CacheMerged, int IndicatorsUpdated, int IndicatorsMerged, int CatalogDeleted, int ReseedRequested);
}
