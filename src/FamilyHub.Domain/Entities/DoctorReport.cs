namespace FamilyHub.Domain.Entities;

/// <summary>
/// Отчёт для врача — снимок данных пациента за период, отрендеренный в PDF. Принадлежит автору
/// (<see cref="OwnerUserId"/>), а пациентом может быть сам автор, подопечный его семьи (человек или
/// питомец) или взрослый член общей семьи — см. <see cref="SubjectUserId"/> / <see cref="SubjectFamilyDependentId"/>.
/// Взрослый пациент видит отчёт о себе в своём списке и может отозвать ссылку.
/// Сам PDF — блоб в хранилище под <see cref="FileAttachment"/> с <c>OwnerType = DoctorReport</c>
/// (шифрование и ротация ключей — общим механизмом вложений); видимости через семью у него нет.
/// Отдать врачу можно публичной ссылкой с токеном: в БД лежит только SHA-256 хеш для поиска
/// (<see cref="ShareTokenHash"/>) и зашифрованный сам токен (<see cref="ShareToken"/>) — чтобы
/// владелец мог скопировать ссылку повторно. Ссылка живёт ограниченное время и отзывается.
/// Без FK на User — как у остальных персональных медданных; чистится явно в AccountService.
/// </summary>
public class DoctorReport
{
    public Guid Id { get; set; }

    /// <summary>Автор отчёта — только он может выдать ссылку и удалить отчёт.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Пациент — взрослый член общей семьи (не автор). Null у обоих Subject-полей — отчёт автора о себе.
    /// Без FK: отчёты о пользователе чистит AccountService при удалении аккаунта.</summary>
    public Guid? SubjectUserId { get; set; }

    /// <summary>Пациент — подопечный семьи (человек или питомец). Без FK: отчёты чистит
    /// FamilyDependentService при удалении подопечного вместе с PDF-блобами.</summary>
    public Guid? SubjectFamilyDependentId { get; set; }

    public DateOnly PeriodFrom { get; set; }

    public DateOnly PeriodTo { get; set; }

    public DateTime CreatedAt { get; set; }

    // Какие блоки вошли в отчёт (галочки при создании).
    public bool IncludeLabs { get; set; }
    public bool IncludeAiSummaries { get; set; }
    public bool IncludeMedications { get; set; }
    public bool IncludeVisits { get; set; }
    public bool IncludeMeasurements { get; set; }
    public bool IncludeSymptomsNotes { get; set; }
    public bool IncludeVaccinations { get; set; }

    public int PageCount { get; set; }

    /// <summary>Для кого отчёт («терапевт Смирнова») — только подпись в списке владельца, в PDF и на
    /// публичную страницу не попадает.</summary>
    [Encrypted]
    public string? Recipient { get; set; }

    /// <summary>Жалобы и вопросы к врачу, как ввёл пациент (заметки дневника с пометкой «в вопросы к
    /// врачу» добавляются в PDF отдельно и здесь не дублируются).</summary>
    [Encrypted]
    public string? PatientComment { get; set; }

    /// <summary>Снимок пациента на момент формирования (ФИО, пол, дата рождения) и список включённых
    /// блоков — JSON. Публичная страница показывает его, а не текущий профиль: она должна совпадать с
    /// PDF, даже если профиль потом изменили.</summary>
    [Encrypted]
    public string PatientSnapshotJson { get; set; } = string.Empty;

    // ---- Ссылка для врача ----

    /// <summary>SHA-256 hex токена — ключ поиска публичной ссылки (unique). Null — ссылки нет:
    /// не создавалась либо отозвана (см. <see cref="ShareRevokedAt"/>).</summary>
    public string? ShareTokenHash { get; set; }

    /// <summary>Сам токен, чтобы владелец мог скопировать ссылку повторно (по хешу его не восстановить).</summary>
    [Encrypted]
    public string? ShareToken { get; set; }

    public DateTime? ShareExpiresAt { get; set; }

    /// <summary>Когда владелец отозвал ссылку. Сбрасывается при выпуске новой.</summary>
    public DateTime? ShareRevokedAt { get; set; }

    /// <summary>Сколько раз открывали текущую ссылку («открыта 2 раза»). Обнуляется с новой ссылкой.</summary>
    public int ShareViewCount { get; set; }

    public DateTime? ShareLastViewedAt { get; set; }
}
