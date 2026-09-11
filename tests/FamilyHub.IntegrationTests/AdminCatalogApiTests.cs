using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
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

    private record AdminRelatedAnalyteMatchDto(string Name, Guid? Id, string? DisplayName, string? SpecimenDisplayName);

    /// <summary>Пикер «Что смотрят вместе» (§ ссылка из справочника вместо свободного текста) —
    /// точное совпадение NormalizedName резолвится в id/displayName, незнакомое имя — Id=null, не
    /// ошибка (оборванная ссылка/опечатка).</summary>
    [Fact]
    public async Task ResolveRelatedAnalytes_ExactMatch_ReturnsId_UnknownName_ReturnsNullId()
    {
        var client = await AuthenticatedClientAsync();
        var normalizedName = $"related{Guid.NewGuid():N}";
        var id = await SeedLabAnalyteAsync(normalizedName, "Связанный показатель", "{}");
        var unknownName = $"нетвсправочнике{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            "/api/admin/kb/lab-analytes/resolve-related", new[] { normalizedName, unknownName });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<List<AdminRelatedAnalyteMatchDto>>();
        results.Should().Contain(r => r.Name == normalizedName && r.Id == id && r.DisplayName == "Связанный показатель");
        results.Should().Contain(r => r.Name == unknownName && r.Id == null);
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

    [Fact]
    public async Task MergeLabAnalytes_RedirectsIndicators_UnionsAliases_DeletesLoser()
    {
        var client = await AuthenticatedClientAsync();
        var specimenId = await SeedSpecimenDirectAsync($"кровь{Guid.NewGuid():N}");
        var loserId = await SeedLabAnalyteAsync($"показательлузер{Guid.NewGuid():N}", "Показатель (дубль)", "{}");
        var winnerId = await SeedLabAnalyteAsync($"показательвиннер{Guid.NewGuid():N}", "Показатель", "{}");
        var loserNormalizedName = await GetNormalizedNameAsync(loserId);

        var recordId = await SeedMedicalRecordAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LabIndicators.Add(new LabIndicator
            {
                Id = Guid.NewGuid(), MedicalRecordId = recordId, RecordDate = new DateOnly(2026, 1, 1),
                OwnerUserId = Guid.NewGuid(), AnalyteKey = loserNormalizedName, DisplayName = "Показатель",
                SpecimenKbId = specimenId, Position = 0, ValueRaw = "1", CreatedAt = DateTime.UtcNow,
                KbAnalyteId = loserId,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/api/admin/kb/lab-analytes/{loserId}/merge-into/{winnerId}", null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verifyDb.GlobalLabAnalytesKb.AnyAsync(k => k.Id == loserId)).Should().BeFalse("проигравшая строка удаляется после мерджа");

        var winnerDetail = await (await client.GetAsync($"/api/admin/kb/lab-analytes/{winnerId}"))
            .Content.ReadFromJsonAsync<AdminLabAnalyteDetailDto>();
        winnerDetail!.Aliases.Should().Contain(loserNormalizedName,
            "старое название проигравшего должно попасть в алиасы победителя — то же название после следующего OCR не даст новый дубль");

        var indicator = await verifyDb.LabIndicators.SingleAsync(i => i.SpecimenKbId == specimenId);
        indicator.KbAnalyteId.Should().Be(winnerId, "показатель должен переехать на победителя");
    }

    [Fact]
    public async Task MergeLabAnalytes_SameId_ReturnsConflict()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedLabAnalyteAsync($"показатель{Guid.NewGuid():N}", "Показатель", "{}");

        var response = await client.PostAsync($"/api/admin/kb/lab-analytes/{id}/merge-into/{id}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private async Task<Guid> SeedSpecimenDirectAsync(string displayName)
    {
        using var scope = factory.Services.CreateScope();
        var specimens = scope.ServiceProvider.GetRequiredService<GlobalSpecimenKbService>();
        return await specimens.FindOrRegisterAsync(displayName, LabAnalyteNormalizer.Normalize(displayName));
    }

    /// <summary>LabIndicators.MedicalRecordId — реальный FK на MedicalRecords, нужна настоящая
    /// строка записи, не произвольный Guid.</summary>
    private async Task<Guid> SeedMedicalRecordAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.MedicalRecords.Add(new MedicalRecord
        {
            Id = id, OwnerUserId = Guid.NewGuid(), Kind = MedicalRecordKind.Analysis,
            RecordDate = new DateOnly(2026, 1, 1), ExtractionStatus = ExtractionStatus.Ready, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Реальный кейс из бага (§ мердж дублей): "Эякулят" + "Физические свойства Эякулят" —
    /// три источника в одной проблеме, здесь достаточно двух, чтобы проверить весь путь редиректа.</summary>
    [Fact]
    public async Task MergeSpecimens_RedirectsAllReferences_UnionsAlias_DeletesLoser()
    {
        var client = await AuthenticatedClientAsync();
        var winnerId = await SeedSpecimenDirectAsync($"Эякулят{Guid.NewGuid():N}");
        var loserId = await SeedSpecimenDirectAsync($"Физические свойства эякулята {Guid.NewGuid():N}");
        var loserNormalizedName = await GetSpecimenNormalizedNameAsync(loserId);

        var analyteId = await SeedLabAnalyteAsync($"объём{Guid.NewGuid():N}", "Объём", "{}");
        var recordId = await SeedMedicalRecordAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.GlobalLabAnalytesKb.Where(k => k.Id == analyteId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.SpecimenKbId, loserId));
            db.LabIndicators.Add(new LabIndicator
            {
                Id = Guid.NewGuid(), MedicalRecordId = recordId, RecordDate = new DateOnly(2026, 1, 1),
                OwnerUserId = Guid.NewGuid(), AnalyteKey = $"объём{Guid.NewGuid():N}", DisplayName = "Объём",
                SpecimenKbId = loserId, Position = 0, ValueRaw = "3", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/api/admin/kb/specimens/{loserId}/merge-into/{winnerId}", null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verifyDb.GlobalSpecimensKb.AnyAsync(s => s.Id == loserId)).Should().BeFalse();
        (await verifyDb.GlobalLabAnalytesKb.Where(k => k.Id == analyteId).Select(k => k.SpecimenKbId).SingleAsync())
            .Should().Be(winnerId);
        (await verifyDb.LabIndicators.Where(i => i.SpecimenKbId == winnerId).AnyAsync()).Should().BeTrue(
            "показатель должен переехать на источник-победитель");

        var winnerAliases = await GetSpecimenAliasesAsync(winnerId);
        winnerAliases.Should().Contain(loserNormalizedName,
            "старое название проигравшего должно попасть в алиасы победителя, иначе следующий такой же OCR-дубль появится снова");
    }

    [Fact]
    public async Task MergeSpecimens_SentinelAsLoser_ReturnsConflict()
    {
        var client = await AuthenticatedClientAsync();
        var winnerId = await SeedSpecimenDirectAsync($"Кровь{Guid.NewGuid():N}");

        var response = await client.PostAsync($"/api/admin/kb/specimens/{SpecimenContextIds.Unresolved}/merge-into/{winnerId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task MergeSpecimens_SameId_ReturnsConflict()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedSpecimenDirectAsync($"Кровь{Guid.NewGuid():N}");

        var response = await client.PostAsync($"/api/admin/kb/specimens/{id}/merge-into/{id}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task MergeSpecimens_ThenFindOrRegisterWithOldName_ResolvesToWinner_ViaAlias()
    {
        var winnerId = await SeedSpecimenDirectAsync($"Эякулят{Guid.NewGuid():N}");
        var loserDisplayName = $"Физические свойства эякулята {Guid.NewGuid():N}";
        var loserId = await SeedSpecimenDirectAsync(loserDisplayName);

        using (var scope = factory.Services.CreateScope())
        {
            var specimens = scope.ServiceProvider.GetRequiredService<GlobalSpecimenKbService>();
            var result = await specimens.MergeAsync(loserId, winnerId);
            result.Should().Be(SpecimenMergeResult.Ok);
        }

        // То же "грязное" название приходит снова (например, повторный OCR того же бланка) —
        // теперь оно должно найти победителя через Aliases, а не завести новый дубль.
        using var verifyScope = factory.Services.CreateScope();
        var verifySpecimens = verifyScope.ServiceProvider.GetRequiredService<GlobalSpecimenKbService>();
        var resolvedId = await verifySpecimens.FindOrRegisterAsync(loserDisplayName, LabAnalyteNormalizer.Normalize(loserDisplayName));

        resolvedId.Should().Be(winnerId, "мердж должен запомнить старое название как алиас победителя");
    }

    private async Task<string> GetSpecimenNormalizedNameAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GlobalSpecimensKb.Where(s => s.Id == id).Select(s => s.NormalizedName).SingleAsync();
    }

    private record SpecimenAliasesRow(string[] Aliases);

    private async Task<string[]> GetSpecimenAliasesAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Database.SqlQuery<SpecimenAliasesRow>(
            $"""SELECT "Aliases" FROM kb.global_specimens_kb WHERE "Id" = {id}""").SingleAsync();
        return row.Aliases;
    }
}
