namespace FamilyHub.Domain.Entities;

/// <summary>
/// Сессия админ-панели (ADR-0009). В cookie — только защищённый Data Protection id этой строки
/// (AdminSessionCookie); сама строка нужна, чтобы выход и «выйти на всех устройствах» отзывали сессию
/// на сервере и это переживало рестарт API (аудит security-audit-2026-10, бэклог M3 — раньше реестр
/// отозванных сессий жил в памяти процесса). Логин у панели один, поэтому без UserId.
/// </summary>
public class AdminSession
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }
}
