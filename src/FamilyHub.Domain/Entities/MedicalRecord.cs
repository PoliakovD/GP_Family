using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Мед-запись — персональный ресурс. Принадлежит пользователю (OwnerUserId), НЕ семье.
/// По умолчанию приватен. НЕ реализует IFamilyOwned — видимость определяется
/// FamilyMedicalShare + MedicalRecordHidden, а не ролью в семье.
/// ПДн-поля шифруются at-rest (этап 2): в БД запись обезличена до OwnerUserId (UUID).
/// Двух видов (Kind): анализ или посещение врача — единая таблица, единый контур доступа
/// (шаринг/скрытие/аудит/вложения не различают вид, см. MedicalRecordService).
/// </summary>
public class MedicalRecord
{
    public Guid Id { get; set; }

    /// <summary>Владелец записи. Только он управляет шарингом и скрытием.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Анализ или посещение врача. Не шифруется — по нему фильтруются списки и поиск в SQL.</summary>
    public MedicalRecordKind Kind { get; set; }

    public DateOnly RecordDate { get; set; }

    [Encrypted]
    public string? Doctor { get; set; }

    /// <summary>Короткое название записи ("Общий анализ крови") — ветка medicalrecords, редизайн
    /// v2. Заполняется распознаванием (ExtractionResult.SuggestedTitle, экстрактор видит шапку
    /// бланка и обычно может назвать анализ прямо оттуда) либо вручную; null, пока не распознано
    /// и не введено. [Encrypted] по той же причине, что Doctor/Description.</summary>
    [Encrypted]
    public string? Title { get; set; }

    [Encrypted]
    public string? Description { get; set; }

    /// <summary>Источник (биоматериал/исследование) ВСЕЙ записи (заметка 1 — один анализ, один
    /// источник, не по показателю: смешанный бланк кровь+моча пользователь разделяет вручную на
    /// две записи). Денормализуется на каждый LabIndicator записи (см.
    /// LabIndicator.SpecimenKbId) — инвариант: всегда равен этому полю. Заполняется автоматически
    /// резолвером (SpecimenResolver) при первом успешном распознавании ИЛИ вручную пользователем;
    /// последующие прогоны «Распознать» не переопределяют уже резолвленный источник (тот же
    /// принцип, что у Title/Doctor ниже по конвейеру — не затираем то, что уже определено).
    /// Никогда не null — нерезолвленный источник указывает на SpecimenContextIds.Unresolved.</summary>
    public Guid SpecimenKbId { get; set; } = SpecimenContextIds.Unresolved;

    /// <summary>Обобщённое слово источника без локализации ("мазок", "соскоб"), которое модель
    /// увидела в документе, но не смогла уточнить, откуда именно (заметка 2) — источник в этом
    /// случае НЕ регистрируется в общем справочнике (голое "мазок" не проходит без места), запись
    /// остаётся с SpecimenKbId=Unresolved, а UI просит пользователя уточнить локализацию именно
    /// этим словом-подсказкой. Null, если источник уже резолвлен или ничего похожего не найдено.</summary>
    [Encrypted]
    public string? SpecimenHint { get; set; }

    /// <summary>Структурированный результат распознавания заключения врача (Kind=DoctorVisit,
    /// VisitConclusion). [Encrypted] ⇒ хранится строкой, не jsonb: SQL-фильтрация по содержимому
    /// невозможна по построению (ADR-0002).</summary>
    [Encrypted]
    public string? ExtractedDataJson { get; set; }

    public ExtractionStatus ExtractionStatus { get; set; }

    /// <summary>LLM-сводка по документу (ветка medicalrecords, задача 5.2): простым языком +
    /// отклонения + вопросы врачу. [Encrypted] — та же логика, что у ExtractedDataJson: SQL по
    /// содержимому невозможен по построению. Null, пока распознавание не запускалось или
    /// суммаризатор не прошёл антигаллюцинационный гейт (см. LabSummarizer) — отсутствие summary
    /// не блокирует отображение самих показателей.</summary>
    [Encrypted]
    public string? SummaryJson { get; set; }

    /// <summary>Подопечный (ребёнок/питомец/пожилой родственник без своего User), для которого
    /// загружена эта запись. Видна всей активной семье подопечного автоматически — см.
    /// MedicalRecordService.VisibleRecordsQuery. Взаимоисключимо с TargetUserId (проверяется в
    /// MedicalRecordService.CreateAsync). FK на FamilyDependent с DELETE CASCADE — см.
    /// MedicalRecordConfiguration.</summary>
    public Guid? FamilyDependentId { get; set; }

    /// <summary>Полноценный член семьи, для которого другой участник загрузил эту запись — видна
    /// ему напрямую, без L1-шаринга. OwnerUserId при этом остаётся за тем, кто физически
    /// загрузил: правило собственности на файл и безусловного удаления не зависит от того, для
    /// кого запись. FK-less, как и OwnerUserId — тот же осознанный выбор (см. комментарий у
    /// индекса OwnerUserId в MedicalRecordConfiguration). Взаимоисключимо с FamilyDependentId.</summary>
    public Guid? TargetUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
