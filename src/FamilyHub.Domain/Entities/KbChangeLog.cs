using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Журнал изменений записей справочников и кэша поиска (ADR-0018): кто/когда/было/стало, с откатом
/// одной версии. Живёт в схеме kb рядом с самими справочниками — хранит только обезличенные снимки
/// строк kb (никакого персонального контекста, см. KbIsolationGuardTests). Пишется raw SQL-ом
/// (KbChangeLogService), чтобы запись журнала не флашила чужие изменения общего DbContext.
/// </summary>
public class KbChangeLog
{
    public Guid Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>"admin" — правка из админки; "system" — автообогащение. Админ платформы — единая учётка без
    /// строки User, поэтому «кто» различает только эти две роли.</summary>
    public string Actor { get; set; } = "system";

    public KbChangeTarget Target { get; set; }

    /// <summary>Id строки справочника/кэша, на которую повлияло изменение (строки может уже не быть).</summary>
    public Guid TargetId { get; set; }

    /// <summary>Читаемое название на момент изменения — чтобы журнал был понятен и после удаления строки.</summary>
    public string TargetLabel { get; set; } = string.Empty;

    /// <summary>ai-write | admin-edit | admin-delete | admin-merge | verify | revert | cache-add | cache-remove |
    /// cache-pin | cache-override | cache-replace | cache-merge.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Снимок строки ДО изменения (JSON); null — строки не было (создание).</summary>
    public string? BeforeJson { get; set; }

    /// <summary>Снимок строки ПОСЛЕ изменения (JSON); null — строку удалили.</summary>
    public string? AfterJson { get; set; }

    public string? Note { get; set; }

    /// <summary>Если запись — откат, id отменённой записи журнала.</summary>
    public Guid? RevertedLogId { get; set; }
}
