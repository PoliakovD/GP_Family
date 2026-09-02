using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Один фоновый прогон пересборки справочника лабораторных показателей (пересборка enrich-
/// пайплайна, §4.2 плана) — запускается вручную из админки после исправления кода очистки
/// имён/резолвинга источника (см. LabAnalyteNameCleaner/SpecimenResolver), чтобы применить их к
/// уже накопленным "грязным" данным задним числом, не только к новым распознаваниям.
/// Инфраструктурное состояние, не медданные — живёт в схеме public, как EncryptionRotationRun,
/// зеркало которого этот тип и есть (тот же приём: резюмируемый курсор в самой строке, переживает
/// рестарт процесса, не более одного активного прогона одновременно).
/// </summary>
public class KbRebuildRun
{
    public Guid Id { get; set; }

    public KbRebuildStatus Status { get; set; } = KbRebuildStatus.Running;

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? LastError { get; set; }

    public int Attempts { get; set; }

    /// <summary>0=перекейовка кэша сниппетов, 1=пересчёт показателей, 2=очистка справочника,
    /// 3=пересев обогащения, 4=готово — резюмируемый курсор ПО ЭТАПАМ, не постраничный внутри
    /// каждого (личные таблицы этого конвейера на порядки меньше EncryptionRotationRun.FieldEntityTypes,
    /// один проход по всей таблице — не проблема).</summary>
    public int StageIndex { get; set; }

    /// <summary>Строк кэша сниппетов, схлопнувшихся при перенормализации NormalizedName — старая
    /// (менее свежая) версия удалена, см. LabAnalyteKbRebuildJob.RekeySearchCacheAsync.</summary>
    public int CacheMerged { get; set; }

    /// <summary>Показателей, у которых пересчитан AnalyteKey/DisplayName/RawDisplayName.</summary>
    public int IndicatorsUpdated { get; set; }

    /// <summary>Показателей одной записи, схлопнувшихся на новом ключе (AnalyteKey, SpecimenKbId) —
    /// дубликат удалён, победил с непустым значением/меньшим Position.</summary>
    public int IndicatorsMerged { get; set; }

    /// <summary>Строк kb.global_lab_analytes_kb, удалённых на этапе очистки.</summary>
    public int CatalogDeleted { get; set; }

    /// <summary>Задач обогащения, поставленных на пересев — по одной на уникальную пару
    /// (AnalyteKey, SpecimenKbId) среди резолвленных источников.</summary>
    public int ReseedRequested { get; set; }
}
