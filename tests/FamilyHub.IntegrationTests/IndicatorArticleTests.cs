using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>GET /api/indicators/{id}/article — редизайн v2, панель справки по клику на показатель.
/// KbAnalyteId не выставляется через публичное API (только конвейером распознавания) — тесты
/// проставляют его напрямую в БД, тот же приём, что и остальные тесты этого файла для
/// LabIndicator/MedicalRecord (см. IndicatorCrudApiTests).</summary>
public class IndicatorArticleTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record MeDto(Guid UserId);

    private Guid? _bloodSpecimenId;

    private async Task<Guid> BloodSpecimenIdAsync()
    {
        _bloodSpecimenId ??= await SeedSpecimenAsync("Кровь");
        return _bloodSpecimenId.Value;
    }

    private async Task<CreateIndicatorRequest> HemoglobinAsync(string value) =>
        new("Гемоглобин", value, "г/л", await BloodSpecimenIdAsync(), "130", "160", null);

    private async Task<Guid> CreateAnalysisAsync(HttpClient owner, DateOnly date)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records", new CreateMedicalRecordRequest(date, null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!.Id;
    }

    private async Task SetOwnerIdentityAsync(Guid userId, DateOnly birthDate, Gender gender)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        user.BirthDate = birthDate;
        user.Gender = gender;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedKbAnalyteAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        // NormalizedName намеренно не совпадает с тем, что сеет Article_PregnancyOnlyRange_...
        // ниже в этом же файле (тоже "гемоглобин", но под другим SpecimenKbId) — LinkIndicatorToKbAsync
        // привязывает по Id напрямую, само название/источник здесь не участвуют в лукапе, поэтому
        // достаточно, чтобы ключ (NormalizedName, SpecimenKbId) не совпал с другой строкой теста.
        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = id,
            NormalizedName = $"гемоглобин{Guid.NewGuid():N}",
            DisplayName = "Гемоглобин",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                plainExplanation = "Белок, переносящий кислород.",
                refRanges = new[]
                {
                    new { ageFrom = (int?)null, ageTo = (int?)null, sex = "male", low = 130.0, high = 160.0, unit = "г/л" },
                    new { ageFrom = (int?)null, ageTo = (int?)null, sex = "female", low = 120.0, high = 150.0, unit = "г/л" },
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

    private async Task LinkIndicatorToKbAsync(Guid indicatorId, Guid kbAnalyteId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indicator = await db.LabIndicators.FirstAsync(i => i.Id == indicatorId);
        indicator.KbAnalyteId = kbAnalyteId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Article_WithKbAnalyteId_ReturnsMatchedRangeForPatientSex()
    {
        var owner = ClientAs(FreshTelegramId());
        var ownerUserId = (await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts))!.UserId;
        await SetOwnerIdentityAsync(ownerUserId, new DateOnly(1990, 1, 1), Gender.Female);
        var kbId = await SeedKbAnalyteAsync();

        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        var indicator = (await (await owner.PostAsJsonAsync($"/api/medical-records/{recordId}/indicators", await HemoglobinAsync("140")))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;
        await LinkIndicatorToKbAsync(indicator.Id, kbId);

        var article = await owner.GetFromJsonAsync<IndicatorArticleResponse>($"/api/indicators/{indicator.Id}/article", JsonOpts);

        article!.Patient.Sex.Should().Be(Gender.Female);
        article.Patient.AgeYears.Should().Be(36);
        article.Article.Should().NotBeNull();
        article.MatchedRefRangeIndex.Should().Be(1, "второй диапазон в payload — female (120–150)");
        article.Article!.RefRanges[article.MatchedRefRangeIndex!.Value].Sex.Should().Be(Gender.Female);
    }

    [Fact]
    public async Task Article_WithoutKbAnalyteId_ReturnsNullArticle_ButStillSucceeds()
    {
        var owner = ClientAs(FreshTelegramId());
        var recordId = await CreateAnalysisAsync(owner, DateOnly.FromDateTime(DateTime.UtcNow));
        var indicator = (await (await owner.PostAsJsonAsync($"/api/medical-records/{recordId}/indicators", await HemoglobinAsync("140")))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;

        var response = await owner.GetAsync($"/api/indicators/{indicator.Id}/article");

        response.EnsureSuccessStatusCode();
        var article = await response.Content.ReadFromJsonAsync<IndicatorArticleResponse>(JsonOpts);
        article!.Article.Should().BeNull("KbAnalyteId не проставлен — справка ещё не заполнена, но панель всё равно открывается");
        article.MatchedRefRangeIndex.Should().BeNull();
        article.Indicator.Id.Should().Be(indicator.Id);
    }

    [Fact]
    public async Task Article_HistoryAvailable_FalseForSinglePoint_TrueOnceSecondPointExists()
    {
        var owner = ClientAs(FreshTelegramId());
        var record1 = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        var indicator1 = (await (await owner.PostAsJsonAsync($"/api/medical-records/{record1}/indicators", await HemoglobinAsync("140")))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;

        var firstArticle = await owner.GetFromJsonAsync<IndicatorArticleResponse>($"/api/indicators/{indicator1.Id}/article", JsonOpts);
        firstArticle!.HistoryAvailable.Should().BeFalse("пока есть только одна точка того же показателя");

        var record2 = await CreateAnalysisAsync(owner, new DateOnly(2026, 2, 1));
        await owner.PostAsJsonAsync($"/api/medical-records/{record2}/indicators", await HemoglobinAsync("145"));

        var secondArticle = await owner.GetFromJsonAsync<IndicatorArticleResponse>($"/api/indicators/{indicator1.Id}/article", JsonOpts);
        secondArticle!.HistoryAvailable.Should().BeTrue("появилась вторая точка того же показателя/биоматериала");
    }

    /// <summary>Пересборка enrich-пайплайна (B9): NormKind/Population должны реально доехать через
    /// KbRefRangeDto → KbReferenceRange до IndicatorFlagCalculator.PickBestRangeIndex — иначе
    /// строка нормы для беременных могла бы ошибочно выбраться как "лучшее совпадение" для
    /// пациентки, о беременности которой в системе никакого сигнала нет.</summary>
    [Fact]
    public async Task Article_PregnancyOnlyRange_NeverAutoMatched_GeneralRangeWinsInstead()
    {
        var owner = ClientAs(FreshTelegramId());
        var ownerUserId = (await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts))!.UserId;
        await SetOwnerIdentityAsync(ownerUserId, new DateOnly(1990, 1, 1), Gender.Female);

        var specimenId = await BloodSpecimenIdAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
            {
                Id = Guid.NewGuid(),
                NormalizedName = "гемоглобин",
                SpecimenKbId = specimenId,
                DisplayName = "Гемоглобин",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 4,
                    refRanges = new[]
                    {
                        new
                        {
                            ageFrom = (int?)null, ageTo = (int?)null, sex = "female", low = 200.0, high = 250.0,
                            unit = "г/л", normKind = "fixed", population = "pregnancy", populationDetail = (string?)"3 триместр",
                            sourceDomain = "invitro.ru", sourceRank = 0,
                        },
                        new
                        {
                            ageFrom = (int?)null, ageTo = (int?)null, sex = "female", low = 120.0, high = 150.0,
                            unit = "г/л", normKind = "fixed", population = "general", populationDetail = (string?)null,
                            sourceDomain = "invitro.ru", sourceRank = 0,
                        },
                    },
                }),
                Source = "тест",
                PayloadVersion = 4,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        var kbId = await FindKbIdAsync("гемоглобин", specimenId);

        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        var indicator = (await (await owner.PostAsJsonAsync($"/api/medical-records/{recordId}/indicators", await HemoglobinAsync("140")))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;
        await LinkIndicatorToKbAsync(indicator.Id, kbId);

        var article = await owner.GetFromJsonAsync<IndicatorArticleResponse>($"/api/indicators/{indicator.Id}/article", JsonOpts);

        article!.Article.Should().NotBeNull();
        var matched = article.Article!.RefRanges[article.MatchedRefRangeIndex!.Value];
        matched.Low.Should().Be(120.0, "строка для беременных не должна автоматически выбираться — нет сигнала о беременности");
        matched.Population.Should().Be(LabPopulation.General);
        matched.SourceDomain.Should().Be("invitro.ru");
    }

    private async Task<Guid> FindKbIdAsync(string normalizedName, Guid specimenKbId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.GlobalLabAnalytesKb
            .Where(k => k.NormalizedName == normalizedName && k.SpecimenKbId == specimenKbId)
            .OrderByDescending(k => k.CreatedAt)
            .FirstAsync()).Id;
    }

    [Fact]
    public async Task Article_StrangerWithNoAccess_ReturnsForbidden()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());
        var recordId = await CreateAnalysisAsync(owner, DateOnly.FromDateTime(DateTime.UtcNow));
        var indicator = (await (await owner.PostAsJsonAsync($"/api/medical-records/{recordId}/indicators", await HemoglobinAsync("140")))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;

        var response = await stranger.GetAsync($"/api/indicators/{indicator.Id}/article");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
