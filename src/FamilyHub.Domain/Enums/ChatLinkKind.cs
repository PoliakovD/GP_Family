namespace FamilyHub.Domain.Enums;

/// <summary>На что может ссылаться сообщение чата (ADR-0019 п. 5). Разрешение в карточку — IChatLinkResolver.</summary>
public enum ChatLinkKind
{
    /// <summary>Мед-запись (анализ или приём врача) — персональная, видимость по MedicalRecordService.</summary>
    MedicalRecord = 0,

    /// <summary>Курс приёма лекарств — видимость по MedicationCourseAccess (ADR-0015/0017).</summary>
    MedicationCourse = 1,

    /// <summary>Аптечка — семейный ресурс, видна активным членам её семьи.</summary>
    Medkit = 2,
}
