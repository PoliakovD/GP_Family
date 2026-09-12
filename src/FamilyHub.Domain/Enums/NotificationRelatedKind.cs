namespace FamilyHub.Domain.Enums;

/// <summary>
/// Что представляет собой <see cref="Entities.Notification.RelatedEntityId"/> — нужен для
/// клик-через на фронте (notifications-tab.component.ts, openRelated), потому что один и тот же
/// GUID-тип поля исторически указывал то на медзапись, то на строку справочника лекарств
/// (KbId — до этого поля вообще не вёл никуда, см. коммит с чипом «уточняем норму…»). Null —
/// старые типы оповещений (MedicationExpiring/BirthdayUpcoming и т.п.), для которых устоявшегося
/// целевого экрана в рамках этой задачи не заводим — карточка остаётся некликабельной.
/// </summary>
public enum NotificationRelatedKind
{
    /// <summary>RelatedEntityId — Id мед-записи вида "анализ" (MedicalRecordKind.Analysis) → /health/records/:id.</summary>
    MedicalRecordAnalysis = 0,

    /// <summary>RelatedEntityId — Id мед-записи вида "посещение врача" (MedicalRecordKind.DoctorVisit) → /health/visits/:id.</summary>
    MedicalRecordVisit = 1,

    /// <summary>RelatedEntityId — Id аптечки (Medkit, НЕ Medication — экран открытой аптечки один на все медикаменты) → /health/medications/:id.</summary>
    Medkit = 2,
}
