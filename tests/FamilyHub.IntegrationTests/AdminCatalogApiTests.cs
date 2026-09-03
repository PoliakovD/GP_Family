using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Ручная правка справочников после ИИ из админки (§3 плана) — редактирование, лок полей,
/// удаление показателей/медикаментов/источников. Реальный Postgres нужен по той же причине, что
/// у остальных kb-тестов: Aliases/LockedFields — text[] вне EF-модели, upsert-CASE в
/// LabAnalyteKbWriter/KbWriter — Postgres-специфичный raw SQL.
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class AdminCatalogApiTests(AdminWebFactory factory)
{
    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    private async Task<Guid> SeedLabAnalyteAsync(string normalizedName, string displayName, string payloadJson)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = id, NormalizedName = normalizedName, DisplayName = displayName, PayloadJson = payloadJson,
            Source = "тест", PayloadVersion = 3, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedMedicationAsync(string normalizedName, string displayName, string payloadJson)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.GlobalMedicationsKb.Add(new GlobalMedicationKb
        {
            Id = id, NormalizedName = normalizedName, DisplayName = displayName, PayloadJson = payloadJson,
            Source = "тест", PayloadVersion = 1, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<LabAnalyteKbWriter> WriterAsync()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LabAnalyteKbWriter>();
    }

    private record AdminLabAnalyteDetailDto(
        Guid Id, string NormalizedName, Guid SpecimenKbId, string? SpecimenDisplayName, string DisplayName,
        string PayloadJson, string Source, List<string> Aliases, List<string> LockedFields, int PayloadVersion,
        DateTime CreatedAt, DateTime UpdatedAt);

    private record AdminMedicationDetailDto(
        Guid Id, string NormalizedName, string DisplayName, string PayloadJson, string Source,
        List<string> Aliases, List<string> LockedFields, int PayloadVersion, DateTime CreatedAt, DateTime UpdatedAt);

    [Fact]
    public async Task LabAnalytes_WithoutSession_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/admin/kb/lab-analytes");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetLabAnalyte_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/admin/kb/lab-analytes/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateLabAnalyte_DisplayName_LocksIt_SurvivesReenrichment()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedLabAnalyteAsync(
            $"локтест{Guid.NewGuid():N}", "Исходное имя", """{"plainExplanation":"старое объяснение"}""");

        var editResponse = await client.PutAsJsonAsync(
            $"/api/admin/kb/lab-analytes/{id}", new { displayName = "Правленное вручную имя" });
        editResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var edited = await editResponse.Content.ReadFromJsonAsync<AdminLabAnalyteDetailDto>();
        edited!.LockedFields.Should().Contain("displayName");
        edited.DisplayName.Should().Be("Правленное вручную имя");

        // Симулируем повторный проход автообогащения тем же ключом — DisplayName залочен, должен
        // пережить апсерт нетронутым, хотя суммаризатор предлагает совсем другое имя.
        var writer = await WriterAsync();
        var summary = new LabAnalyteSummary(
            null, null, "новое объяснение от ИИ", null, null, null, [], [], [0]);
        var normalized = await GetNormalizedNameAsync(id);
        await writer.UpsertAsync(normalized, SpecimenContextIds.Unresolved, "Имя от автообогащения", summary, "тест-2");

        var afterReenrich = await client.GetFromJsonAsync<AdminLabAnalyteDetailDto>($"/api/admin/kb/lab-analytes/{id}");
        afterReenrich!.DisplayName.Should().Be("Правленное вручную имя", "залоченное поле не должно перезаписаться повторным обогащением");
    }

    [Fact]
    public async Task UnlockField_ThenReenrich_FieldUpdatesAgain()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedLabAnalyteAsync(
            $"локтест2{Guid.NewGuid():N}", "Старое имя", "{}");

        await client.PutAsJsonAsync($"/api/admin/kb/lab-analytes/{id}", new { displayName = "Залоченное имя" });

        var unlockResponse = await client.DeleteAsync($"/api/admin/kb/lab-analytes/{id}/locks/displayName");
        unlockResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var writer = await WriterAsync();
        var normalized = await GetNormalizedNameAsync(id);
        var summary = new LabAnalyteSummary(null, null, "текст", null, null, null, [], [], [0]);
        await writer.UpsertAsync(normalized, SpecimenContextIds.Unresolved, "Имя после разлочки", summary, "тест-3");

        var after = await client.GetFromJsonAsync<AdminLabAnalyteDetailDto>($"/api/admin/kb/lab-analytes/{id}");
        after!.DisplayName.Should().Be("Имя после разлочки");
    }

    [Fact]
    public async Task UpdateLabAnalyte_InvalidPayloadJson_ReturnsBadRequest()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedLabAnalyteAsync($"локтест3{Guid.NewGuid():N}", "Имя", "{}");

        var response = await client.PutAsJsonAsync($"/api/admin/kb/lab-analytes/{id}", new { payloadJson = "не json{{{" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateLabAnalyte_PersonalContextInPayload_IsRejected()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedLabAnalyteAsync($"локтест4{Guid.NewGuid():N}", "Имя", "{}");

        var response = await client.PutAsJsonAsync(
            $"/api/admin/kb/lab-analytes/{id}",
            new { payloadJson = """{"whyMeasured":"Уточнить у ivan.petrov@example.com"}""" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteLabAnalyte_RemovesRow()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedLabAnalyteAsync($"локтест5{Guid.NewGuid():N}", "Имя", "{}");

        var deleteResponse = await client.DeleteAsync($"/api/admin/kb/lab-analytes/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync($"/api/admin/kb/lab-analytes/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateMedication_DisplayName_LocksIt()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedMedicationAsync($"медтест{Guid.NewGuid():N}", "Исходное", "{}");

        var response = await client.PutAsJsonAsync($"/api/admin/kb/medications/{id}", new { displayName = "Правленное" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<AdminMedicationDetailDto>();
        detail!.LockedFields.Should().Contain("displayName");
        detail.DisplayName.Should().Be("Правленное");
    }

    [Fact]
    public async Task Specimens_RenameToExistingName_ReturnsConflict()
    {
        var client = await AuthenticatedClientAsync();
        var uniqueA = $"специмен-а-{Guid.NewGuid():N}";
        var uniqueB = $"специмен-б-{Guid.NewGuid():N}";

        using var scope = factory.Services.CreateScope();
        var specimens = scope.ServiceProvider.GetRequiredService<GlobalSpecimenKbService>();
        var idA = await specimens.FindOrRegisterAsync(uniqueA, LabAnalyteNormalizer.Normalize(uniqueA));
        var idB = await specimens.FindOrRegisterAsync(uniqueB, LabAnalyteNormalizer.Normalize(uniqueB));

        var response = await client.PutAsJsonAsync($"/api/admin/kb/specimens/{idB}", new { displayName = uniqueA });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Specimens_DeleteSentinelUnresolved_ReturnsConflict()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.DeleteAsync($"/api/admin/kb/specimens/{SpecimenContextIds.Unresolved}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Specimens_DeleteInUse_ReturnsConflict_DeleteUnused_Succeeds()
    {
        var client = await AuthenticatedClientAsync();
        var uniqueUsed = $"специмен-used-{Guid.NewGuid():N}";
        var uniqueUnused = $"специмен-unused-{Guid.NewGuid():N}";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var specimens = scope.ServiceProvider.GetRequiredService<GlobalSpecimenKbService>();
        var usedId = await specimens.FindOrRegisterAsync(uniqueUsed, LabAnalyteNormalizer.Normalize(uniqueUsed));
        var unusedId = await specimens.FindOrRegisterAsync(uniqueUnused, LabAnalyteNormalizer.Normalize(uniqueUnused));

        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = Guid.NewGuid(), NormalizedName = $"показатель{Guid.NewGuid():N}", SpecimenKbId = usedId,
            DisplayName = "Показатель", PayloadJson = "{}", Source = "тест", PayloadVersion = 3,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var usedResponse = await client.DeleteAsync($"/api/admin/kb/specimens/{usedId}");
        usedResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var unusedResponse = await client.DeleteAsync($"/api/admin/kb/specimens/{unusedId}");
        unusedResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<string> GetNormalizedNameAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GlobalLabAnalytesKb.Where(k => k.Id == id).Select(k => k.NormalizedName).SingleAsync();
    }
}
