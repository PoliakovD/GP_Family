using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Одна ротация учётки приложения к Postgres или MinIO (ADR-0011) — только МЕТАДАННЫЕ. Сами секреты
/// (пароль, secret key) НИГДЕ в БД не хранятся: они показываются администратору один раз при
/// генерации и живут только в PROD_ENV (ADR-0009 §«Последствия» — секреты не в БД). Идентификаторы
/// (имя роли Postgres, access key MinIO) секретом не являются.
/// </summary>
public class CredentialRotation
{
    public Guid Id { get; set; }

    public CredentialKind Kind { get; set; }

    /// <summary>Учётка, под которой приложение работало на момент генерации: имя роли Postgres либо
    /// access key MinIO.</summary>
    public string FromIdentity { get; set; } = string.Empty;

    /// <summary>Новая учётка: имя роли Postgres либо access key MinIO.</summary>
    public string ToIdentity { get; set; } = string.Empty;

    public CredentialRotationStatus Status { get; set; } = CredentialRotationStatus.AwaitingDeploy;

    public DateTime GeneratedAt { get; set; }

    /// <summary>Кто сгенерировал (имя админ-учётки панели).</summary>
    public string GeneratedBy { get; set; } = string.Empty;

    /// <summary>Когда админ-панель впервые увидела, что приложение работает под <see cref="ToIdentity"/>.</summary>
    public DateTime? ActivatedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string? RevokedBy { get; set; }

    /// <summary>Postgres: сколько открытых сессий старой роли оборвано при отзыве (у MinIO — null).</summary>
    public int? TerminatedSessions { get; set; }
}
