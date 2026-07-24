using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FamilyHub.Modules.Medical.Search;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Этап 3 (ADR-0003): единый /api/search — гибрид Postgres-FTS (лекарства, справочник KB) и
/// in-memory поиска (медкарты, т.к. поля зашифрованы at-rest, ADR-0002). Реальный Postgres
/// (Testcontainers, FamilyHubWebFactory) — только на нём применяется миграция AddFullTextSearch
/// (tsvector/pg_trgm/GIN), SQLite-юнит-тесты её не видят.
/// </summary>
public class SearchApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);

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

    private static Task<SearchResponse?> SearchAsync(HttpClient client, string q, string? types = null)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(q)}";
        if (types is not null) url += $"&types={Uri.EscapeDataString(types)}";
        return client.GetFromJsonAsync<SearchResponse>(url, JsonOpts);
    }

    [Fact]
    public async Task Search_FindsMedication_ByWordForm_Postgres_Tsvector()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);
        await admin.PostAsJsonAsync($"/api/medkits/{medkitId}/medications",
            new CreateMedicationRequest("Ибупрофен", null, new Dictionary<string, string> { ["dosage"] = "200 мг" }));

        // Родительный падеж — другое словоформа, находится через to_tsvector('russian', ...).
        var response = await SearchAsync(admin, "ибупрофена");

        response!.Items.Should().ContainSingle(i => i.Type == SearchResultType.Medication && i.Title == "Ибупрофен");
    }

    [Fact]
    public async Task Search_FindsMedication_ByTypo_Postgres_Trigram()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);
        await admin.PostAsJsonAsync($"/api/medkits/{medkitId}/medications",
            new CreateMedicationRequest("Парацетамол", null, null));

        // Опечатка OCR (пропущена буква) — находится через pg_trgm similarity(), не через tsvector.
        var response = await SearchAsync(admin, "парацетмол");

        response!.Items.Should().ContainSingle(i => i.Type == SearchResultType.Medication && i.Title == "Парацетамол");
    }

    [Fact]
    public async Task Search_Medication_IsScopedToOwnFamilies_NotVisibleToOutsider()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);
        await admin.PostAsJsonAsync($"/api/medkits/{medkitId}/medications",
            new CreateMedicationRequest("Уникальныйпрепарат77", null, null));
        var outsider = ClientAs(FreshTelegramId());

        var response = await SearchAsync(outsider, "уникальныйпрепарат77");

        response!.Items.Should().BeEmpty("лекарство чужой семьи не должно попадать в результаты поиска");
    }

    [Fact]
    public async Task Search_FindsGlobalKnowledgeBaseEntry_Postgres_Tsvector()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GlobalMedicationsKb.Add(new GlobalMedicationKb
            {
                Id = Guid.NewGuid(),
                NormalizedName = "аскорбиновая кислота",
                DisplayName = "Аскорбиновая кислота",
                PayloadJson = """{"composition":"витамин C"}""",
                Source = "ГРЛС",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Любой вошедший с принятым согласием видит обезличенный справочник — доступ не завязан на семью.
        var user = ClientAs(FreshTelegramId());

        var response = await SearchAsync(user, "аскорбиновой кислоты");

        response!.Items.Should().ContainSingle(i => i.Type == SearchResultType.Kb && i.Title == "Аскорбиновая кислота");
    }

    [Fact]
    public async Task Search_FindsOwnMedicalRecord_InMemory_DespiteAtRestEncryption()
    {
        var owner = ClientAs(FreshTelegramId());
        await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(
                "Иван Иванов", DateOnly.FromDateTime(DateTime.UtcNow), "Терапевт",
                "Общий анализ крови: гемоглобин снижен", null));

        // Поле Description зашифровано at-rest (ADR-0002) — Postgres-FTS по нему невозможен;
        // находится только через in-memory поиск после расшифровки в scope владельца (ADR-0003).
        var response = await SearchAsync(owner, "гемоглобин");

        response!.Items.Should().ContainSingle(i => i.Type == SearchResultType.Record);
    }

    [Fact]
    public async Task Search_MedicalRecord_NotVisibleToStranger_NotSharedWithFamily()
    {
        var owner = ClientAs(FreshTelegramId());
        await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(
                "Пётр Петров", DateOnly.FromDateTime(DateTime.UtcNow), null,
                "Направление к эндокринологу, подозрение на диабет", null));
        var stranger = ClientAs(FreshTelegramId());

        var response = await SearchAsync(stranger, "диабет");

        response!.Items.Should().BeEmpty("чужая нерасшаренная медкарта не должна находиться поиском");
    }

    [Fact]
    public async Task Search_MedicalRecord_VisibleToFamilyAfterShare_HiddenAfterHide()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        await admin.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(
                "Семейный Пациент", DateOnly.FromDateTime(DateTime.UtcNow), null,
                "Консультация кардиолога по поводу давления", null));

        var beforeShare = await SearchAsync(admin, "кардиолог");
        beforeShare!.Items.Should().ContainSingle(i => i.Type == SearchResultType.Record);

        await admin.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId));
        var record = beforeShare.Items.Single(i => i.Type == SearchResultType.Record);

        await admin.PostAsJsonAsync($"/api/medical-records/{record.Id}/hide", new FamilyIdsRequest([familyId]));

        // Скрыто от собственной семьи владельца — но сам владелец продолжает находить свою запись.
        var afterHide = await SearchAsync(admin, "кардиолог");
        afterHide!.Items.Should().ContainSingle(i => i.Type == SearchResultType.Record);
    }

    [Fact]
    public async Task Search_ShortOrEmptyQuery_ReturnsEmptyWithoutError()
    {
        var user = ClientAs(FreshTelegramId());

        var httpResponse = await user.GetAsync("/api/search?q=а");
        httpResponse.EnsureSuccessStatusCode();
        var response = await httpResponse.Content.ReadFromJsonAsync<SearchResponse>(JsonOpts);

        response!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_TypesFilter_ScopesToRequestedSourcesOnly()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);
        const string token = "уникальныйтокен777";

        // Один и тот же токен — и в лекарстве, и в медкарте: без фильтра находятся оба типа.
        await admin.PostAsJsonAsync($"/api/medkits/{medkitId}/medications",
            new CreateMedicationRequest(token, null, null));
        await admin.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(
                "Тестовый Пациент", DateOnly.FromDateTime(DateTime.UtcNow), null, token, null));

        var unfiltered = await SearchAsync(admin, token);
        unfiltered!.Items.Select(i => i.Type).Should().BeEquivalentTo(
            [SearchResultType.Medication, SearchResultType.Record],
            "без фильтра оба совпавших источника должны попасть в выдачу");

        var medicationOnly = await SearchAsync(admin, token, types: "medication");
        medicationOnly!.Items.Should().OnlyContain(i => i.Type == SearchResultType.Medication,
            "types=medication не должен возвращать совпавшую медкарту — это отдельный (дорогой, in-memory) источник");

        var recordOnly = await SearchAsync(admin, token, types: "record");
        recordOnly!.Items.Should().OnlyContain(i => i.Type == SearchResultType.Record);
    }

    [Fact]
    public async Task Migration_CreatesExpectedExtensionAndGinIndexes()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var extensionExists = await db.Database.SqlQuery<int>(
                $"SELECT 1 AS \"Value\" FROM pg_extension WHERE extname = 'pg_trgm'")
            .AnyAsync();
        extensionExists.Should().BeTrue("миграция AddFullTextSearch должна включить pg_trgm");

        var ginIndexNames = await db.Database.SqlQuery<string>($"""
            SELECT indexname AS "Value" FROM pg_indexes
            WHERE schemaname IN ('medical', 'kb') AND indexdef ILIKE '%USING gin%'
            """).ToListAsync();

        ginIndexNames.Should().Contain(new[]
        {
            "IX_Medications_search_vector", "IX_Medications_Name_trgm",
            "IX_global_medications_kb_search_vector", "IX_global_medications_kb_DisplayName_trgm",
        });
    }
}
