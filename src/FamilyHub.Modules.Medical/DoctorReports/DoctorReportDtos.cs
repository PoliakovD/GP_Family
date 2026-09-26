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
    string? Recipient,
    string? PatientComment,
    /// <summary>7, 14 или 30 — сразу выпустить ссылку на столько дней; null — без ссылки.</summary>
    int? ShareDays);

public record ShareDoctorReportRequest(int Days);

public record DoctorReportBlocksDto(
    bool Labs, bool AiSummaries, bool Medications, bool Visits, bool Measurements, bool SymptomsNotes);

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
    DoctorReportLinkDto Link);

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
    IReadOnlyList<string> Sections);

/// <summary>Снимок пациента и состава отчёта на момент формирования (DoctorReport.PatientSnapshotJson).</summary>
public record ReportSnapshot(string FullName, string? Sex, DateOnly? BirthDate, IReadOnlyList<string> Sections);
