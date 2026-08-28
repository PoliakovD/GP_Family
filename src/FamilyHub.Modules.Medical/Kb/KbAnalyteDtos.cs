using System.Text.Json.Serialization;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>Одна карточка в списке результатов справочника показателей (редизайн v2,
/// /health/kb/indicators) — зеркало KbListItem (медикаменты), другое поле вместо Purpose.</summary>
public record KbAnalyteListItem(Guid Id, string DisplayName, string? PlainExplanation);

public record KbAnalyteListResponse(IReadOnlyList<KbAnalyteListItem> Items, bool HasMore);

public record KbRefRangeDto(int? AgeFrom, int? AgeTo, Gender? Sex, double? Low, double? High, string? Unit);

/// <summary>Id=null — статьи по этому имени пока нет в справочнике (обогащение ещё не дошло до
/// него) — чип рендерится, но некликабелен. Живой резолв, не хранимая ссылка — та же причина,
/// что у PrescribedMedicationDto.KbMedicationId.</summary>
public record KbRelatedAnalyte(Guid? Id, string DisplayName);

/// <summary>Полная статья справочника показателей — панель/шторка справки (редизайн v2).
/// UpdatedAt — дата обновления для дисклеймера ("статья knowledge base, обновлена …"). Aliases
/// сознательно не отдаётся — тот же выбор, что уже сделан для KbMedicationCard: синонимы нужны
/// только внутреннему механизму сопоставления, не отображению.</summary>
public record KbAnalyteCard(
    Guid Id,
    string DisplayName,
    string? LoincCode,
    string? DefaultUnit,
    string? PlainExplanation,
    string? WhyMeasured,
    string? HighMeans,
    string? LowMeans,
    IReadOnlyList<KbRefRangeDto> RefRanges,
    IReadOnlyList<KbRelatedAnalyte> Related,
    string Source,
    DateTime UpdatedAt);

/// <summary>Форма PayloadJson (см. LabAnalyteKbPayload.Build) — только для десериализации на
/// чтении карточки; malformed JSON не должен ронять запрос (см. KbAnalyteCatalogService).</summary>
internal sealed record KbAnalytePayloadDto(
    [property: JsonPropertyName("loincCode")] string? LoincCode,
    [property: JsonPropertyName("defaultUnit")] string? DefaultUnit,
    [property: JsonPropertyName("plainExplanation")] string? PlainExplanation,
    [property: JsonPropertyName("whyMeasured")] string? WhyMeasured,
    [property: JsonPropertyName("highMeans")] string? HighMeans,
    [property: JsonPropertyName("lowMeans")] string? LowMeans);

/// <summary>Проекция raw SQL для списка (см. KbAnalyteCatalogService.SearchAsync).</summary>
internal sealed class KbAnalyteCatalogRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
}

/// <summary>Проекция raw SQL для карточки (см. KbAnalyteCatalogService.GetByIdAsync).</summary>
internal sealed class KbAnalyteDetailRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Source { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Проекция raw SQL для резолва «Что смотрят вместе» (см.
/// KbAnalyteCatalogService.ResolveRelatedAsync). Обычный internal class, НЕ file-scoped — EF Core
/// SqlQuery&lt;T&gt; не умеет строить keyless entity type для file-scoped класса (падает с
/// IndexOutOfRangeException внутри NavigationExpandingExpressionVisitor при непустой выборке).</summary>
internal sealed class KbRelatedMatchRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
}
