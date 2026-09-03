using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// RecalculateIndicatorFlagsJob — "дозаполнение задним числом" после того, как
/// LabAnalyteEnrichmentProcessor наполнил справочник (см. class doc). Регрессионный тест на
/// реальный баг: показатель с RefSource.Blank (референс напечатан прямо в бланке — самый частый
/// случай на практике) никогда не привязывался к статье справочника (LabIndicator.KbAnalyteId),
/// даже когда обогащение благополучно завершалось, потому что кандидатов выбирали строго по
/// RefSource.None — панель справки навсегда показывала "нет данных" вместо готовой статьи.
/// </summary>
public class RecalculateIndicatorFlagsJobTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private async Task<Guid> CreateAnalysisAsync(HttpClient owner, DateOnly date)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records", new CreateMedicalRecordRequest(date, null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!.Id;
    }

    private async Task<Guid> SeedKbAnalyteAsync(string normalizedName, Guid specimenId, double low, double high)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = id,
            NormalizedName = normalizedName,
            SpecimenKbId = specimenId,
            DisplayName = "Гемоглобин",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                plainExplanation = "Белок, переносящий кислород.",
                refRanges = new[]
                {
                    new { ageFrom = (int?)null, ageTo = (int?)null, sex = (string?)null, low, high, unit = "г/л" },
                },
            }),
            Source = "тест",
            PayloadVersion = 3,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<IndicatorSnapshot> ReadIndicatorAsync(Guid indicatorId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var i = await db.LabIndicators.AsNoTracking().SingleAsync(x => x.Id == indicatorId);
        return new IndicatorSnapshot(i.KbAnalyteId, i.RefSource, i.Flag, i.RefLowText, i.RefHighText);
    }

    private record IndicatorSnapshot(Guid? KbAnalyteId, RefSource RefSource, IndicatorFlag Flag, string? RefLowText, string? RefHighText);

    [Fact]
    public async Task RunAsync_IndicatorWithBlankRefSource_LinksKbAnalyteId_ButDoesNotOverrideFormRange()
    {
        var owner = ClientAs(FreshTelegramId());
        var specimenId = await SeedSpecimenAsync($"Кровь {Guid.NewGuid():N}");
        // Пробел ПЕРЕД суффиксом обязателен — LabAnalyteNormalizer.FixMixedScriptHomoglyphs
        // разбирает слова по пробелу и подменяет латинские гомоглифы ТОЛЬКО внутри слова, где уже
        // есть кириллица; без пробела "Гемоглобин3fa85f64" стало бы одним словом со смешанным
        // алфавитом, и некоторые латинские hex-символы (a/c/e/…) молча подменились бы кириллицей —
        // вычисленный здесь normalizedName разошёлся бы с тем, что реально сохранит API.
        var rawName = $"Гемоглобин {Guid.NewGuid():N}";
        var normalizedName = LabAnalyteNormalizer.Normalize(rawName);

        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        // refLow/refHigh заданы напрямую — тот же путь, что бланк, печатающий свой референс:
        // IndicatorFlagCalculator.Calculate сразу даёт RefSource.Blank (см. ExtractionQueryService.CreateIndicatorAsync).
        var createResponse = await owner.PostAsJsonAsync($"/api/medical-records/{recordId}/indicators",
            new CreateIndicatorRequest(rawName, "140", "г/л", specimenId, "130", "160", null));
        createResponse.EnsureSuccessStatusCode();
        var indicator = (await createResponse.Content.ReadFromJsonAsync<IndicatorDto>())!;

        var before = await ReadIndicatorAsync(indicator.Id);
        before.KbAnalyteId.Should().BeNull();
        before.RefSource.Should().Be(RefSource.Blank, "у показателя с прямым референсом из формы сразу RefSource.Blank");
        before.Flag.Should().Be(IndicatorFlag.Normal, "140 внутри диапазона бланка 130-160");

        // KB-диапазон намеренно ДРУГОЙ, чем у бланка — если бы баг вернулся (Flag/RefSource
        // пересчитывались бы по KB), Flag сменился бы на High, хотя бланк говорит Normal.
        var kbId = await SeedKbAnalyteAsync(normalizedName, specimenId, low: 100, high: 120);

        using (var scope = Factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<RecalculateIndicatorFlagsJob>();
            await job.RunAsync(kbId);
        }

        var after = await ReadIndicatorAsync(indicator.Id);
        after.KbAnalyteId.Should().Be(kbId, "привязка к статье не должна зависеть от RefSource — регрессия найденного бага");
        after.RefSource.Should().Be(RefSource.Blank, "референс из бланка в приоритете и не переопределяется справочником");
        after.Flag.Should().Be(IndicatorFlag.Normal, "флаг остаётся посчитанным по диапазону бланка, не по диапазону KB");
        after.RefLowText.Should().Be(before.RefLowText);
        after.RefHighText.Should().Be(before.RefHighText);
    }

    [Fact]
    public async Task RunAsync_IndicatorWithNoneRefSource_LinksKbAnalyteId_AndRecalculatesFlag()
    {
        var owner = ClientAs(FreshTelegramId());
        var specimenId = await SeedSpecimenAsync($"Кровь {Guid.NewGuid():N}");
        var rawName = $"Гемоглобин {Guid.NewGuid():N}";
        var normalizedName = LabAnalyteNormalizer.Normalize(rawName);

        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        // Без refLow/refHigh/refText — RefSource.None, ждёт справочник (прежнее, уже рабочее поведение).
        var createResponse = await owner.PostAsJsonAsync($"/api/medical-records/{recordId}/indicators",
            new CreateIndicatorRequest(rawName, "140", "г/л", specimenId, null, null, null));
        createResponse.EnsureSuccessStatusCode();
        var indicator = (await createResponse.Content.ReadFromJsonAsync<IndicatorDto>())!;

        var before = await ReadIndicatorAsync(indicator.Id);
        before.RefSource.Should().Be(RefSource.None);
        before.Flag.Should().Be(IndicatorFlag.Unknown);

        var kbId = await SeedKbAnalyteAsync(normalizedName, specimenId, low: 100, high: 120);

        using (var scope = Factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<RecalculateIndicatorFlagsJob>();
            await job.RunAsync(kbId);
        }

        var after = await ReadIndicatorAsync(indicator.Id);
        after.KbAnalyteId.Should().Be(kbId);
        after.RefSource.Should().Be(RefSource.KbFixed);
        after.Flag.Should().Be(IndicatorFlag.High, "140 выше диапазона KB (100-120)");
    }
}
