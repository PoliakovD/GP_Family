using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>Полная строка справочника показателей для редактирования из админки (§3 плана) —
/// в отличие от KbAnalyteCard (публичная карточка для пользователя), несёт сырой PayloadJson
/// целиком, LockedFields и Aliases — то, что нужно только редактору, не обычному читателю.</summary>
public record AdminLabAnalyteDetail(
    Guid Id, string NormalizedName, Guid SpecimenKbId, string? SpecimenDisplayName, string DisplayName,
    string PayloadJson, string Source, IReadOnlyList<string> Aliases, IReadOnlyList<string> LockedFields,
    int PayloadVersion, DateTime CreatedAt, DateTime UpdatedAt,
    KbVerificationStatus VerificationStatus = KbVerificationStatus.AiUnverified, DateTime? VerifiedAt = null,
    bool VerificationStale = false);

/// <summary>Зеркало AdminLabAnalyteDetail на справочник медикаментов.</summary>
public record AdminMedicationDetail(
    Guid Id, string NormalizedName, string DisplayName, string PayloadJson, string Source,
    IReadOnlyList<string> Aliases, IReadOnlyList<string> LockedFields, int PayloadVersion,
    DateTime CreatedAt, DateTime UpdatedAt,
    KbVerificationStatus VerificationStatus = KbVerificationStatus.AiUnverified, DateTime? VerifiedAt = null,
    bool VerificationStale = false);

/// <summary>Поле, присланное в теле PUT (не null), автоматически лочится — см. AdminCatalogService.
/// Aliases — null означает "не трогать", пустой список — явно очистить. LockedPayloadKeys — режим
/// формы (§4 плана «Форма ⇄ JSON»): если задан вместе с PayloadJson, лочатся отдельные
/// "payload.&lt;key&gt;" (реально изменённые поля формы), а не весь "payload" целиком, как при
/// null (обычная правка сырого JSON). Игнорируется, если PayloadJson не прислан.</summary>
public record AdminKbEditRequest(
    string? DisplayName, string? PayloadJson, IReadOnlyList<string>? Aliases,
    IReadOnlyList<string>? LockedPayloadKeys = null);

public enum AdminKbEditResult { Ok, NotFound, InvalidPayloadJson, IsolationViolation }

/// <summary>Строка списка справочника в админке (ADR-0018): те же поля, что публичный список, плюс внутренний
/// статус проверки — пользователям он не отдаётся. Preview — plainExplanation (показатели) / purpose (препараты).
/// VerificationStale — проверенный payload с тех пор изменился (проверка устарела).</summary>
public record AdminKbListItem(
    Guid Id, string DisplayName, Guid? SpecimenKbId, string? SpecimenDisplayName, string? Preview,
    KbVerificationStatus VerificationStatus, bool VerificationStale, DateTime UpdatedAt);

public record AdminKbListResponse(IReadOnlyList<AdminKbListItem> Items, bool HasMore);

/// <summary>Режим фильтра списка по статусу проверки: все / только непроверенные / только проверенные.</summary>
public enum AdminKbVerificationFilter { All = 0, Unverified = 1, Verified = 2 }

/// <summary>«Проверено X из Y» по одному справочнику. Verified = всё, кроме AiUnverified.</summary>
public record KbVerificationCounts(int Total, int AiUnverified, int AdminVerified, int AdminEdited, int ManualKnowledge)
{
    public int Verified => AdminVerified + AdminEdited + ManualKnowledge;
}

public record AdminKbVerificationSummary(KbVerificationCounts LabAnalytes, KbVerificationCounts Medications);

public enum AdminRevertResult { Ok, NotFound, AlreadyReverted, NothingToRevert, CannotRevertRevert, Conflict }

/// <summary>Мердж двух строк справочника (§ ручной мердж дублей из админки) — SameId, если
/// прислали одну и ту же строку и победителем, и проигравшим.</summary>
public enum AdminKbMergeResult { Ok, NotFound, SameId }

/// <summary>Conflict — у нового биоматериала уже есть статья с тем же названием: вместо дубля админу
/// предлагается объединить статьи (решение владельца, 2026-10-04).</summary>
public enum AdminSpecimenChangeResult { Ok, NotFound, SpecimenNotFound, Conflict }

public record AdminSpecimenChangeConflict(Guid ExistingId, string ExistingDisplayName);

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
    public int VerificationStatus { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedPayloadHash { get; set; }
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
    public int VerificationStatus { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedPayloadHash { get; set; }
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
