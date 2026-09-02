using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Редизайн v2 — GET /api/medical-records/{recordId}/indicators/{indicatorId}/history:
/// тренд показателя для КОНКРЕТНОЙ записи (в отличие от свои-only GET /api/indicators/{analyteKey}),
/// нужен для «Динамики» в панели справки по расшаренной записи. Самый чувствительный тест плана
/// редизайна (риск Р5) — наивная реализация (фильтр только по владельцу) была бы обходом
/// точечного L2-скрытия (MedicalRecordHidden): тренд по ОДНОЙ расшаренной записи раскрыл бы
/// значения из ДРУГИХ записей того же владельца, точечно скрытых именно от этой семьи.</summary>
public class IndicatorHistoryVisibilityTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record MeDto(Guid UserId);

    private Guid? _bloodSpecimenId;

    private async Task<CreateIndicatorRequest> HemoglobinAsync(string value)
    {
        _bloodSpecimenId ??= await SeedSpecimenAsync("Кровь");
        return new("Гемоглобин", value, "г/л", _bloodSpecimenId.Value, "130", "160", null);
    }

    private static async Task<MedicalRecordDto> CreateAnalysisAsync(HttpClient owner, DateOnly date)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records", new CreateMedicalRecordRequest(date, null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
    }

    private async Task<Guid> CreateFamilyAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(JsonOpts);
        return body!["id"];
    }

    [Fact]
    public async Task History_ExcludesRecordsHiddenFromViewer_ButOwnerSeesAll()
    {
        var owner = ClientAs(FreshTelegramId());
        var viewer = ClientAs(FreshTelegramId());

        // Общая семья: viewer вступает через инвайт и сразу одобряется — активное членство
        // обязательно для L1-видимости.
        var familyId = await CreateFamilyAsync(owner);
        var invite = await (await owner.PostAsJsonAsync($"/api/families/{familyId}/invites",
                new { TargetUserId = (Guid?)null, AssignedRole = FamilyRole.Member, MaxUses = 5, ExpiresAt = (DateTime?)null }))
            .Content.ReadFromJsonAsync<Dictionary<string, object>>(JsonOpts);
        var code = invite!["code"].ToString();
        await viewer.PostAsync($"/api/invites/{code}/redeem", null);
        var viewerUserId = (await viewer.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts))!.UserId;
        (await owner.PostAsync($"/api/families/{familyId}/members/{viewerUserId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Две записи одного владельца, тот же показатель (Гемоглобин/Кровь — общий AnalyteKey+Specimen).
        // L1 (FamilyMedicalShare) — грант "владелец → семья" на ВСЕ записи владельца этого вида,
        // не точечный список; единственный механизм точечного исключения — L2 (MedicalRecordHidden).
        // Значит "никогда не расшаренная" запись внутри уже расшаренной семьи невозможна как сценарий —
        // тестируем именно L2-исключение, это и есть риск Р5 плана.
        var visible = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        var hiddenFromFamily = await CreateAnalysisAsync(owner, new DateOnly(2026, 2, 1));

        var visibleIndicator = (await (await owner.PostAsJsonAsync($"/api/medical-records/{visible.Id}/indicators", await HemoglobinAsync("140")))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;
        await owner.PostAsJsonAsync($"/api/medical-records/{hiddenFromFamily.Id}/indicators", await HemoglobinAsync("150"));

        // L1: расшарить ВСЕ записи семье. L2: точечно скрыть hiddenFromFamily именно от неё.
        (await owner.PostAsJsonAsync("/api/medical-records/share", new { FamilyId = familyId }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsJsonAsync($"/api/medical-records/{hiddenFromFamily.Id}/hide", new { FamilyIds = new[] { familyId } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Viewer запрашивает тренд ЧЕРЕЗ доступную ему запись (visible) — не должен увидеть
        // значение из hiddenFromFamily (это и было бы обходом L2).
        var viewerHistory = await viewer.GetFromJsonAsync<List<IndicatorHistoryPoint>>(
            $"/api/medical-records/{visible.Id}/indicators/{visibleIndicator.Id}/history", JsonOpts);

        viewerHistory.Should().ContainSingle(p => p.ValueRaw == "140");
        viewerHistory.Should().NotContain(p => p.ValueRaw == "150", "hiddenFromFamily точечно скрыта именно от этой семьи (L2)");

        // Владелец по той же самой записи видит ОБЕ точки — L1/L2 ограничивают только чужой
        // просмотр, не собственный.
        var ownerHistory = await owner.GetFromJsonAsync<List<IndicatorHistoryPoint>>(
            $"/api/medical-records/{visible.Id}/indicators/{visibleIndicator.Id}/history", JsonOpts);
        ownerHistory.Should().HaveCount(2);
    }

    [Fact]
    public async Task History_StrangerWithNoAccessToRecord_ReturnsForbidden()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner, DateOnly.FromDateTime(DateTime.UtcNow));
        var indicator = (await (await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", await HemoglobinAsync("140")))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;

        var response = await stranger.GetAsync($"/api/medical-records/{record.Id}/indicators/{indicator.Id}/history");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
