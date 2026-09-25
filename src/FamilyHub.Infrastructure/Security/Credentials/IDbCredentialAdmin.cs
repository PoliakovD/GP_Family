namespace FamilyHub.Infrastructure.Security.Credentials;

/// <param name="SessionUser">Роль, под которой приложение вошло в Postgres (session_user; current_user
/// всегда familyhub_owner, см. ADR-0011).</param>
/// <param name="RotationFunctionsPresent">Есть ли схема familyhub_admin с функциями ротации — то есть
/// выполнена ли первичная настройка (bootstrap-app-credentials.sh).</param>
public sealed record DbSessionInfo(string SessionUser, bool RotationFunctionsPresent);

/// <summary>Один из двух слотов входа приложения (familyhub_app_a / familyhub_app_b).</summary>
public sealed record DbRoleSlot(string RoleName, bool CanLogin, bool HasPassword, int ActiveSessions);

/// <summary>
/// Управление ролями входа приложения в Postgres (ADR-0011) — только через три SECURITY DEFINER-функции
/// схемы familyhub_admin (см. deploy/scripts/sql/bootstrap-roles.sql): у самого приложения нет прав на
/// управление ролями, и оно не может тронуть ничего, кроме familyhub_app_a/b.
/// </summary>
public interface IDbCredentialAdmin
{
    Task<DbSessionInfo> GetSessionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DbRoleSlot>> GetSlotsAsync(CancellationToken ct = default);

    /// <summary>Ставит пароль (готовым SCRAM-верификатором) и включает вход соседней роли.</summary>
    Task SetPasswordAsync(string role, string scramVerifier, CancellationToken ct = default);

    /// <summary>Запрещает вход, обнуляет пароль и обрывает открытые сессии роли; возвращает их число.</summary>
    Task<int> DisableAsync(string role, CancellationToken ct = default);
}
