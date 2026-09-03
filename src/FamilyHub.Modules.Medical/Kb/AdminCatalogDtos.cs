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
/// Aliases — null означает "не трогать", пустой список — явно очистить.</summary>
public record AdminKbEditRequest(string? DisplayName, string? PayloadJson, IReadOnlyList<string>? Aliases);

public enum AdminKbEditResult { Ok, NotFound, InvalidPayloadJson, IsolationViolation }

public enum AdminKbDeleteResult { Ok, NotFound }

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
