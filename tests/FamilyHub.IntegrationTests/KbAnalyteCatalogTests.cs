using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Kb;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>GET /api/kb/analytes[?q] + GET /api/kb/analytes/{id} — редизайн v2, зеркало
/// KbLookupTests (медикаменты) на другую таблицу. Реальный Postgres нужен по той же причине —
/// search_vector/similarity/Aliases недоступны на SQLite-юнит-тестах. Своё уникальное слово на
/// каждый тест этого файла — все они делят один Postgres-контейнер (IntegrationTestCollection),
/// NormalizedName уникален (см. тот же приём в KbLookupTests).</summary>
public class KbAnalyteCatalogTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private async Task<Guid> SeedAsync(
        string normalizedName, string displayName, object payload, string[]? aliases = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = id,
            NormalizedName = normalizedName,
            DisplayName = displayName,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            Source = "тест",
            PayloadVersion = 3,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        if (aliases is { Length: > 0 })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE kb.global_lab_analytes_kb SET "Aliases" = {aliases} WHERE "NormalizedName" = {normalizedName}
                """);
        }
        return id;
    }

    [Fact]
    public async Task Search_ByPlainText_FindsSeededAnalyte()
    {
        await SeedAsync("лейкоциты", "Лейкоциты", new { schemaVersion = 3, plainExplanation = "Белые кровяные клетки." });
        var client = ClientAs(FreshTelegramId());

        var response = await client.GetFromJsonAsync<KbAnalyteListResponse>("/api/kb/analytes?q=лейкоциты&skip=0&take=20", JsonOpts);

        response!.Items.Should().ContainSingle(i => i.DisplayName == "Лейкоциты");
        response.Items.Single().PlainExplanation.Should().Be("Белые кровяные клетки.");
    }

    [Fact]
    public async Task Search_ByAlias_ResolvesToCanonicalDisplayName()
    {
        await SeedAsync("тромбоциты", "Тромбоциты", new { schemaVersion = 3 }, aliases: ["plt"]);
        var client = ClientAs(FreshTelegramId());

        var response = await client.GetFromJsonAsync<KbAnalyteListResponse>("/api/kb/analytes?q=plt&skip=0&take=20", JsonOpts);

        response!.Items.Should().ContainSingle(i => i.DisplayName == "Тромбоциты");
    }

    [Fact]
    public async Task GetById_ReturnsFullCard_WithRefRangesAndRelated_SomeResolvedSomeNot()
    {
        // "Глюкоза" уже существует в справочнике — резолвится в кликабельный чип (ResolveRelatedAsync
        // матчит по NormalizedName, поэтому оно обязано совпадать с LabAnalyteNormalizer.Normalize
        // от имени в relatedNames, не быть произвольным уникальным словом); "Инсулин" не посеян —
        // некликабельный чип (Id=null), обогащение до него ещё не дошло.
        await SeedAsync("глюкоза", "Глюкоза", new { schemaVersion = 3, plainExplanation = "Уровень сахара в крови." });
        var id = await SeedAsync("холестерин", "Холестерин", new
        {
            schemaVersion = 3,
            loincCode = "2093-3",
            defaultUnit = "ммоль/л",
            plainExplanation = "Липид, строительный материал клеточных мембран.",
            whyMeasured = "Оценка риска атеросклероза.",
            highMeans = "Повышенный риск сердечно-сосудистых заболеваний.",
            lowMeans = "Редко клинически значимо.",
            relatedNames = new[] { "Глюкоза", "Инсулин" },
            refRanges = new[]
            {
                new { ageFrom = (int?)null, ageTo = (int?)null, sex = "male", low = 3.5, high = 5.2, unit = "ммоль/л" },
                new { ageFrom = (int?)null, ageTo = (int?)null, sex = "female", low = 3.2, high = 5.0, unit = "ммоль/л" },
            },
        });
        var client = ClientAs(FreshTelegramId());

        var card = await client.GetFromJsonAsync<KbAnalyteCard>($"/api/kb/analytes/{id}", JsonOpts);

        card!.DisplayName.Should().Be("Холестерин");
        card.LoincCode.Should().Be("2093-3");
        card.RefRanges.Should().HaveCount(2);
        card.RefRanges.Should().Contain(r => r.Sex == FamilyHub.Domain.Enums.Gender.Male && r.Low == 3.5 && r.High == 5.2);
        card.Related.Should().ContainSingle(r => r.DisplayName == "Глюкоза" && r.Id != null);
        card.Related.Should().ContainSingle(r => r.DisplayName == "Инсулин" && r.Id == null);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFound()
    {
        var client = ClientAs(FreshTelegramId());

        var response = await client.GetAsync($"/api/kb/analytes/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_PayloadWithWrongFieldShapes_DegradesGracefully_DoesNotFailRequest()
    {
        // jsonb гарантирует синтаксически валидный JSON на запись — "битый" здесь означает валидный
        // JSON-объект с полями неожиданной формы (refRanges/relatedNames не массивы), не синтаксическую
        // ошибку. Именно эту защитную ветку (ValueKind-проверки в LabAnalyteKbPayload.ParseRefRanges/
        // ParseRelatedNames) и проверяем — деградация в пустые списки, не 500.
        var id = await SeedAsync("бракованныйpayload", "Бракованный payload", new
        {
            schemaVersion = 3,
            refRanges = "не массив",
            relatedNames = 123,
        });

        var client = ClientAs(FreshTelegramId());
        var response = await client.GetAsync($"/api/kb/analytes/{id}");

        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<KbAnalyteCard>(JsonOpts);
        card!.PlainExplanation.Should().BeNull();
        card.RefRanges.Should().BeEmpty();
        card.Related.Should().BeEmpty();
    }
}
