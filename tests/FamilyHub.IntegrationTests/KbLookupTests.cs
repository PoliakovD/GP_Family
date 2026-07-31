using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Каскадный поиск в справочнике (этап 4, KbLookupService): точное совпадение → алиас (торговое
/// название) → нечёткое (триграммы + tsvector). Реальный Postgres нужен так же, как и в
/// SearchApiTests — раздельные raw-SQL пороги (0.55 автопривязка) недоступны на SQLite-юнит-тестах.
/// Медикамент создаётся с Enrichment:Provider по умолчанию (Null) — фоновая догонка справочника
/// не мешает: LookupAsync проверяем ДО того, как асинхронный конвейер вообще успел бы что-то дописать.
/// </summary>
public class KbLookupTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record MedicationKbCardDto(string DisplayName);
    private record MedicationKbResponseDto(int Status, MedicationKbCardDto? Card, object? Candidate);

    private const int StatusReady = 4;

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

    private async Task SeedKbAsync(string normalizedName, string displayName, string[]? aliases = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        db.GlobalMedicationsKb.Add(new GlobalMedicationKb
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            DisplayName = displayName,
            PayloadJson = """{"schemaVersion":1}""",
            Source = "тест",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        // Aliases (text[]) — не в EF-модели (см. GlobalMedicationKbConfiguration), пишется raw SQL.
        if (aliases is { Length: > 0 })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE kb.global_medications_kb SET "Aliases" = {aliases} WHERE "NormalizedName" = {normalizedName}
                """);
        }
    }

    private async Task<MedicationKbResponseDto> GetKbStatusAsync(HttpClient client, Guid medicationId)
    {
        var response = await client.GetAsync($"/api/medications/{medicationId}/kb");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MedicationKbResponseDto>(JsonOpts))!;
    }

    [Fact]
    public async Task ExactNormalizedName_MatchesDespiteDosageAndPackaging()
    {
        await SeedKbAsync("парацетамол", "Парацетамол");
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);

        // "Парацетамол 400мг таб. №20" нормализуется к "парацетамол" ещё до похода в справочник.
        var medicationId = await CreateMedicationAsync(admin, medkitId, "Парацетамол 400мг таб. №20");

        var status = await GetKbStatusAsync(admin, medicationId);

        status.Status.Should().Be(StatusReady);
        status.Card!.DisplayName.Should().Be("Парацетамол");
    }

    [Fact]
    public async Task Typo_MatchesOrSuggestsCandidate_ViaTrigramSimilarity()
    {
        // Своё слово на каждый тест этого файла — все они делят один Postgres-контейнер
        // (IntegrationTestCollection), а NormalizedName уникален, см. соседние тесты.
        await SeedKbAsync("ибупрофен", "Ибупрофен");
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);

        // Опечатка OCR (пропущена буква) — как и Search_FindsMedication_ByTypo_Postgres_Trigram,
        // проверяет pg_trgm similarity(), не точный ключ. Порог автопривязки (0.55) строже общего
        // поиска (0.3) — не требуем именно Ready, достаточно, что фаззи-поиск НЕ дал полного промаха.
        var medicationId = await CreateMedicationAsync(admin, medkitId, "ибупрфен");

        var status = await GetKbStatusAsync(admin, medicationId);

        (status.Status == StatusReady || status.Candidate is not null).Should().BeTrue(
            "пропущенная буква должна быть найдена нечётким поиском — либо автопривязкой, либо кандидатом");
    }

    [Fact]
    public async Task TradeNameAlias_ResolvesToInternationalName()
    {
        await SeedKbAsync("кеторолак", "Кеторолак", aliases: ["кетанов"]);
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);

        var medicationId = await CreateMedicationAsync(admin, medkitId, "Кетанов");

        var status = await GetKbStatusAsync(admin, medicationId);

        status.Status.Should().Be(StatusReady);
        status.Card!.DisplayName.Should().Be("Кеторолак", "алиас должен резолвиться к МНН-записи справочника");
    }

    [Fact]
    public async Task UnknownMedication_NeverFalselyMatchesUnrelatedEntries()
    {
        await SeedKbAsync("дротаверин", "Дротаверин");
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);

        // Уникальное имя без пересечения по триграммам с посеянной записью — не должно "случайно" совпасть.
        var medicationId = await CreateMedicationAsync(admin, medkitId, "Уникальныйнесуществующийпрепарат999");

        var status = await GetKbStatusAsync(admin, medicationId);

        status.Status.Should().NotBe(StatusReady, "несуществующий в справочнике препарат не должен получить готовую карточку");
        status.Card.Should().BeNull();
    }
}
