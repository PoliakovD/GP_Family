using System.Text.Json.Serialization;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>Одна карточка в списке результатов (раздел «Справочник» на фронте) — без полного payload,
/// только то, что нужно для списка.</summary>
public record KbListItem(Guid Id, string DisplayName, string? Purpose);

/// <summary>HasMore вместо точного Total (как и SearchService — не считаем отдельным COUNT-запросом
/// при фильтрации по q): фронту для пагинации "показать ещё" достаточно факта, что выдача не пуста
/// и совпадает по размеру со страницей.</summary>
public record KbListResponse(IReadOnlyList<KbListItem> Items, bool HasMore);

/// <summary>Полная карточка препарата — payload + прослеживаемость источника (дисклеймер на фронте,
/// см. G. Фронтенд в плане этапа).</summary>
public record KbMedicationCard(
    Guid Id,
    string DisplayName,
    string? InternationalName,
    IReadOnlyList<string> TradeNames,
    string? Form,
    string? Purpose,
    string? Usage,
    string? Storage,
    string? Driving,
    string? SpecialNotes,
    string Source,
    DateTime UpdatedAt);

/// <summary>Статус обогащения конкретного медикамента пользователя (GET /api/medications/{id}/kb).</summary>
public enum MedicationKbStatus { None, Pending, Running, Failed, Ready }

/// <summary>Card заполнена только при Ready. Candidate — неуверенная нечёткая привязка (см.
/// KbLookupService), которую фронт может предложить пользователю подтвердить вручную, но
/// НЕ показывает как готовый ответ.</summary>
public record MedicationKbResponse(MedicationKbStatus Status, KbMedicationCard? Card, KbCandidate? Candidate);

public record KbCandidate(Guid KbId, string DisplayName, double Score);

/// <summary>Форма PayloadJson, которую пишет KbWriter (см. MedicationSummarizerDtos.MedicationSummary) —
/// используется только для десериализации на чтении, случай малоformed JSON не должен ронять запрос.</summary>
internal sealed record KbPayloadDto(
    [property: JsonPropertyName("schemaVersion")] int? SchemaVersion,
    [property: JsonPropertyName("internationalName")] string? InternationalName,
    [property: JsonPropertyName("tradeNames")] List<string>? TradeNames,
    [property: JsonPropertyName("form")] string? Form,
    [property: JsonPropertyName("purpose")] string? Purpose,
    [property: JsonPropertyName("usage")] string? Usage,
    [property: JsonPropertyName("storage")] string? Storage,
    [property: JsonPropertyName("driving")] string? Driving,
    [property: JsonPropertyName("specialNotes")] string? SpecialNotes);

/// <summary>Проекция raw SQL для списка (см. KbCatalogService.SearchAsync).</summary>
internal sealed class KbCatalogRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
}

/// <summary>Проекция raw SQL для карточки (см. KbCatalogService.GetByIdAsync).</summary>
internal sealed class KbDetailRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Source { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}
