namespace FamilyHub.Infrastructure.Security.Credentials;

public enum MinioCredentialMode
{
    /// <summary>Приложение работает под service account — ротация возможна.</summary>
    ServiceAccount,

    /// <summary>Приложение работает под root/обычным пользователем (dev, либо первичная настройка ещё
    /// не выполнена) — service account'ов у такой учётки нет, ротировать нечего.</summary>
    NotServiceAccount,

    /// <summary>MinIO недоступен или ответил ошибкой сервера — режим определить не удалось.</summary>
    Unknown,
}

/// <summary>
/// Управление service account приложения в MinIO через admin API (ADR-0011). SDK minio-dotnet admin API
/// не содержит, реализация — MinioAdminClient (подпись SigV4 + шифрование тела madmin). Никаких прав
/// сверх выданных service account'у не требуется: MinIO разрешает ему создавать и удалять service
/// account'ы того же родителя (проверено на реальном сервере).
/// </summary>
public interface IMinioCredentialAdmin
{
    /// <summary>Access key, под которым приложение работает СЕЙЧАС (Minio:AccessKey из конфигурации).</summary>
    string CurrentAccessKey { get; }

    Task<MinioCredentialMode> GetModeAsync(CancellationToken ct = default);

    /// <summary>Выпускает service account с заданными access/secret key под тем же родителем, что и
    /// текущий. Ответ сервера (он зашифрован и содержит только эхо выданного) не читается.</summary>
    Task CreateServiceAccountAsync(string accessKey, string secretKey, string name, CancellationToken ct = default);

    /// <summary>Удаляет service account. «Уже нет» (404) — не ошибка: отзыв идемпотентен.</summary>
    Task DeleteServiceAccountAsync(string accessKey, CancellationToken ct = default);
}
