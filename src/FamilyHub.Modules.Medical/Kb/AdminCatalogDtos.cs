namespace FamilyHub.Modules.Medical.Kb;

/// <summary>Полная строка справочника показателей для редактирования из админки (§3 плана) —
/// в отличие от KbAnalyteCard (публичная карточка для пользователя), несёт сырой PayloadJson
/// целиком, LockedFields и Aliases — то, что нужно только редактору, не обычному читателю.</summary>
public record AdminLabAnalyteDetail(
    Guid Id, string NormalizedName, Guid SpecimenKbId, string? SpecimenDisplayName, string DisplayName,
    string PayloadJson, string Source, IReadOnlyList<string> Aliases, IReadOnlyList<string> LockedFields,
    int PayloadVersion, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Зеркало AdminLabAnalyteDetail на справочник медикаментов.</summary>
public record AdminMedicationDetail(
    Guid Id, string NormalizedName, string DisplayName, string PayloadJson, string Source,
    IReadOnlyList<string> Aliases, IReadOnlyList<string> LockedFields, int PayloadVersion,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Поле, присланное в теле PUT (не null), автоматически лочится — см. AdminCatalogService.
/// Aliases — null означает "не трогать", пустой список — явно очистить. LockedPayloadKeys — режим
/// формы (§4 плана «Форма ⇄ JSON»): если задан вместе с PayloadJson, лочатся отдельные
/// "payload.&lt;key&gt;" (реально изменённые поля формы), а не весь "payload" целиком, как при
/// null (обычная правка сырого JSON). Игнорируется, если PayloadJson не прислан.</summary>
public record AdminKbEditRequest(
    string? DisplayName, string? PayloadJson, IReadOnlyList<string>? Aliases,
    IReadOnlyList<string>? LockedPayloadKeys = null);

public enum AdminKbEditResult { Ok, NotFound, InvalidPayloadJson, IsolationViolation }

/// <summary>Мердж двух строк справочника (§ ручной мердж дублей из админки) — SameId, если
/// прислали одну и ту же строку и победителем, и проигравшим.</summary>
public enum AdminKbMergeResult { Ok, NotFound, SameId }

/// <summary>Итог резолва одного related-имени — Id/DisplayName/SpecimenDisplayName все null,
/// если по точному NormalizedName ничего не нашлось (оборванная ссылка/опечатка, не ошибка).</summary>
public record AdminRelatedAnalyteMatch(string Name, Guid? Id, string? DisplayName, string? SpecimenDisplayName);

internal sealed class AdminRelatedMatchRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? SpecimenDisplayName { get; set; }
}

internal sealed class AdminLabAnalyteRow
{
    public Guid Id { get; set; }
    public string NormalizedName { get; set; } = string.Empty;
    public Guid SpecimenKbId { get; set; }
    public string? SpecimenDisplayName { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Source { get; set; } = string.Empty;
    public string[] Aliases { get; set; } = [];
    public string[] LockedFields { get; set; } = [];
    public int PayloadVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

internal sealed class AdminMedicationRow
{
    public Guid Id { get; set; }
    public string NormalizedName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Source { get; set; } = string.Empty;
    public string[] Aliases { get; set; } = [];
    public string[] LockedFields { get; set; } = [];
    public int PayloadVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
