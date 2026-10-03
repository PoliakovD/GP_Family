namespace FamilyHub.Modules.Medical.DoctorReports;

/// <summary>Состояние публичной ссылки на отчёт. Числа — часть контракта с фронтом.</summary>
public enum DoctorReportLinkStatus
{
    /// <summary>Ссылка не создавалась — «только PDF».</summary>
    None = 0,
    Active = 1,
    Expired = 2,
    Revoked = 3,
}

/// <summary>Кто пациент в отчёте. Числа — часть контракта с фронтом.</summary>
public enum DoctorReportSubjectKind
{
    /// <summary>Автор сам.</summary>
    Self = 0,

    /// <summary>Взрослый член общей активной семьи с аккаунтом.</summary>
    User = 1,

    /// <summary>Подопечный семьи без аккаунта — человек или питомец.</summary>
    Dependent = 2,
}

public enum DoctorReportResult
{
    Success,
    NotFound,
    Invalid,

    /// <summary>За период нет данных для выбранных блоков — пустой PDF не строим.</summary>
    NoData,

    /// <summary>Сервис PDF (Gotenberg) не настроен или недоступен.</summary>
    PdfUnavailable,

    /// <summary>Достигнут лимит отчётов на пользователя.</summary>
    TooMany,
}

public record CreateDoctorReportRequest(
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    bool IncludeLabs,
    bool IncludeAiSummaries,
    bool IncludeMedications,
    bool IncludeVisits,
    bool IncludeMeasurements,
    bool IncludeSymptomsNotes,
    bool IncludeVaccinations,
    string? Recipient,
    string? PatientComment,
    /// <summary>7, 14 или 30 — сразу выпустить ссылку на столько дней; null — без ссылки.</summary>
    int? ShareDays,
    /// <summary>Для кого отчёт; без поля — о себе (старые клиенты).</summary>
    DoctorReportSubjectKind SubjectKind = DoctorReportSubjectKind.Self,
    /// <summary>Id пользователя или подопечного; для Self не нужен.</summary>
    Guid? SubjectId = null);

/// <summary>Кандидат в пациенты для формы. DiaryAvailable/VaccinationsAvailable — какие блоки вообще
/// можно собрать: дневник есть только у людей с аккаунтом и чужой — только по гранту «Дневник»,
/// прививки чужого взрослого — по гранту «Прививки», у питомцев прививок в приложении нет.</summary>
public record DoctorReportSubjectDto(
    DoctorReportSubjectKind Kind,
    Guid? Id,
    string Name,
    bool IsPet,
    bool DiaryAvailable,
    bool VaccinationsAvailable);

public record ShareDoctorReportRequest(int Days);

public record DoctorReportBlocksDto(
    bool Labs, bool AiSummaries, bool Medications, bool Visits, bool Measurements, bool SymptomsNotes, bool Vaccinations);

public record DoctorReportLinkDto(
    DoctorReportLinkStatus Status,
    /// <summary>Токен ссылки — фронт строит адрес как {origin}/r/{token}. Есть только у активной и истёкшей.</summary>
    string? Token,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    int ViewCount,
    DateTime? LastViewedAt);

public record DoctorReportDto(
    Guid Id,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateTime CreatedAt,
    int PageCount,
    int BlockCount,
    string? Recipient,
    DoctorReportBlocksDto Blocks,
    DoctorReportLinkDto Link,
    DoctorReportSubjectKind SubjectKind = DoctorReportSubjectKind.Self,
    Guid? SubjectId = null,
    /// <summary>Имя пациента из снимка на момент формирования.</summary>
    string? SubjectName = null,
    bool SubjectIsPet = false,
    /// <summary>Автор, если это не текущий пользователь (отчёт о нём составил член семьи).</summary>
    string? CreatedByName = null,
    /// <summary>Текущий пользователь — автор: может выдать ссылку и удалить. Пациент может только открыть и отозвать.</summary>
    bool CanManage = true);

/// <summary>Что видит врач на публичной странице до открытия PDF. Ничего лишнего: без email, без адресата.</summary>
public record PublicReportMeta(
    string PatientName,
    string? Sex,
    int? Age,
    DateOnly? BirthDate,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    int PageCount,
    IReadOnlyList<string> Sections,
    bool IsPet = false);

/// <summary>Снимок пациента и состава отчёта на момент формирования (DoctorReport.PatientSnapshotJson).</summary>
public record ReportSnapshot(string FullName, string? Sex, DateOnly? BirthDate, IReadOnlyList<string> Sections, bool IsPet = false);
