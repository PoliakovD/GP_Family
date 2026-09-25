namespace FamilyHub.Api.Features.Admin;

/// <summary>Один из двух слотов входа приложения в Postgres (familyhub_app_a / familyhub_app_b).</summary>
public record CredentialSlotDto(string Role, bool CanLogin, bool HasPassword, int ActiveSessions);

/// <summary>Запись истории ротации — только метаданные; секретов здесь нет и никогда не было.
/// <c>Kind</c>: Postgres | Minio; <c>Status</c>: AwaitingDeploy | Activated | Revoked | Superseded.</summary>
public record CredentialRotationDto(
    Guid Id, string Kind, string From, string To, string Status,
    DateTime GeneratedAt, string GeneratedBy, DateTime? ActivatedAt, DateTime? RevokedAt, int? TerminatedSessions);

/// <param name="Mode">LeastPrivilege — приложение работает под familyhub_app_a/b, ротация доступна;
/// Superuser — под суперпользователем (dev либо не выполнена первичная настройка); Unavailable — Postgres не ответил.</param>
/// <param name="SessionUser">Роль, под которой приложение работает сейчас.</param>
/// <param name="CurrentSince">С какого момента панель знает, что приложение работает под этой ролью
/// (null — «с первичной настройки», записей об этом нет).</param>
/// <param name="RevocableOld">Старая роль, которую можно отозвать (приложение уже перешло на новую).</param>
public record PostgresCredentialStatusDto(
    string Mode, string? SessionUser, IReadOnlyList<CredentialSlotDto> Slots,
    DateTime? CurrentSince, CredentialRotationDto? Pending, string? RevocableOld);

/// <param name="Mode">ServiceAccount — ротация доступна; NotServiceAccount — приложение под root/пользователем
/// (dev либо не выполнена первичная настройка); Unknown — MinIO не ответил.</param>
/// <param name="AccessKeyMasked">Access key в маске (первые и последние 4 символа) — целиком не нужен, а
/// по маске видно, какой ключ сейчас в работе.</param>
public record MinioCredentialStatusDto(
    string Mode, string AccessKeyMasked, DateTime? CurrentSince,
    CredentialRotationDto? Pending, string? RevocableOldMasked);

public record CredentialsStatusDto(
    PostgresCredentialStatusDto Postgres, MinioCredentialStatusDto Minio, IReadOnlyList<CredentialRotationDto> History);

/// <summary>Ответ на «Сгенерировать»: строки для PROD_ENV. Показываются администратору ОДИН раз —
/// нигде на сервере не сохраняются (в БД — только метаданные ротации).</summary>
public record GeneratedCredentialDto(Guid RotationId, IReadOnlyList<string> EnvLines);

/// <param name="TerminatedSessions">Postgres: сколько открытых сессий отозванной роли оборвано (MinIO — null).</param>
public record RevokedCredentialDto(int? TerminatedSessions);
