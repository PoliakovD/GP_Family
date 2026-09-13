using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Отвечает "valid: true" на ЛЮБОЙ вызов (что легитимность, что правдоподобность —
/// оба гейта читают одно и то же поле "valid") и считает вызовы — используется, чтобы доказать,
/// что гейт правдоподобности (AnalytePlausibilityGuardService) реально вызывается ТОЛЬКО для
/// EnrichmentRequestOrigin.ManualEntry, а не для Extraction/SystemMaintenance.</summary>
public sealed class CountingAlwaysValidLmStudioJsonClient : ILmStudioJsonClient
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default, bool suppressThinking = false) =>
        ExtractJsonAsync(systemPrompt, userText, ct, suppressThinking);

    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, CancellationToken ct = default, bool suppressThinking = false)
    {
        Interlocked.Increment(ref _callCount);
        var payload = new Dictionary<string, JsonElement>
        {
            ["valid"] = JsonSerializer.SerializeToElement(true),
            ["reason"] = JsonSerializer.SerializeToElement((string?)null),
        };
        return Task.FromResult(new LmStudioJsonResult(true, payload, null));
    }
}

public class LabAnalyteEnrichmentOriginWebFactory : FamilyHubWebFactory
{
    public CountingAlwaysValidLmStudioJsonClient LmStudioClient { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<ILmStudioJsonClient>(LmStudioClient));
    }
}

[CollectionDefinition(Name)]
public class LabAnalyteEnrichmentOriginCollection : ICollectionFixture<LabAnalyteEnrichmentOriginWebFactory>
{
    public const string Name = "LabAnalyteEnrichmentOriginIntegration";
}

/// <summary>
/// Гейт «на бред» (AnalytePlausibilityGuardService) должен вызываться ТОЛЬКО для
/// EnrichmentRequestOrigin.ManualEntry — документное извлечение уже прошло собственный
/// антигаллюцинационный гейт при структурировании, второй слой смысловой проверки ему не нужен
/// (см. class doc AnalytePlausibilityGuardService). Оба сценария бьют на существующую строку
/// GlobalLabAnalyteKb (Hit) — короткое замыкание сразу после гейтов, до веб-поиска/суммаризации,
/// поэтому разница в количестве вызовов ILmStudioJsonClient изолированно показывает именно
/// работу гейта правдоподобности, а не что-то ещё в конвейере.
/// </summary>
[Collection(LabAnalyteEnrichmentOriginCollection.Name)]
public class LabAnalyteEnrichmentOriginTests(LabAnalyteEnrichmentOriginWebFactory factory)
{
    private async Task<(Guid SpecimenId, string AnalyteKey)> SeedKbHitAsync(AppDbContext db)
    {
        var specimenId = Guid.NewGuid();
        db.GlobalSpecimensKb.Add(new GlobalSpecimenKb
        {
            Id = specimenId, NormalizedName = "кровь" + Guid.NewGuid().ToString("N")[..6],
            DisplayName = "Кровь", Source = "тест", CreatedAt = DateTime.UtcNow,
        });

        var analyteKey = "гемоглобин" + Guid.NewGuid().ToString("N")[..6];
        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = Guid.NewGuid(), NormalizedName = analyteKey, SpecimenKbId = specimenId,
            DisplayName = "Гемоглобин", PayloadJson = "{}", Source = "тест",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (specimenId, analyteKey);
    }

    private static LabAnalyteEnrichmentJob NewJob(string analyteKey, Guid specimenId, EnrichmentRequestOrigin origin) => new()
    {
        Id = Guid.NewGuid(), NormalizedName = analyteKey, SpecimenKbId = specimenId,
        SourceDisplayName = analyteKey, RequestedByUserId = Guid.NewGuid(), Origin = origin,
        Status = EnrichmentJobStatus.Pending, CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task ExtractionOrigin_SkipsPlausibilityGate_OnlyLegitimacyChecked()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (specimenId, analyteKey) = await SeedKbHitAsync(db);
        var job = NewJob(analyteKey, specimenId, EnrichmentRequestOrigin.Extraction);
        db.LabAnalyteEnrichmentJobs.Add(job);
        await db.SaveChangesAsync();

        var callsBefore = factory.LmStudioClient.CallCount;
        var processor = scope.ServiceProvider.GetRequiredService<LabAnalyteEnrichmentProcessor>();
        await processor.RunAsync(job.Id);

        (factory.LmStudioClient.CallCount - callsBefore).Should().Be(1,
            "Extraction-происхождение проходит только гейт легитимности, не правдоподобности");

        var completed = await db.LabAnalyteEnrichmentJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        completed.Status.Should().Be(EnrichmentJobStatus.Completed, completed.Error);
    }

    [Fact]
    public async Task ManualEntryOrigin_AlsoRunsPlausibilityGate_TwoCallsTotal()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (specimenId, analyteKey) = await SeedKbHitAsync(db);
        var job = NewJob(analyteKey, specimenId, EnrichmentRequestOrigin.ManualEntry);
        db.LabAnalyteEnrichmentJobs.Add(job);
        await db.SaveChangesAsync();

        var callsBefore = factory.LmStudioClient.CallCount;
        var processor = scope.ServiceProvider.GetRequiredService<LabAnalyteEnrichmentProcessor>();
        await processor.RunAsync(job.Id);

        (factory.LmStudioClient.CallCount - callsBefore).Should().Be(2,
            "ManualEntry проходит и гейт легитимности, и гейт правдоподобности — два отдельных LLM-вызова");

        var completed = await db.LabAnalyteEnrichmentJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        completed.Status.Should().Be(EnrichmentJobStatus.Completed, completed.Error);
    }
}
