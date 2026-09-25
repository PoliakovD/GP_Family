using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security.Credentials;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Admin;

public enum CredentialFailure
{
    /// <summary>Приложение работает не под familyhub_app_a/b (dev либо не выполнена первичная настройка).</summary>
    NotLeastPrivilege,

    /// <summary>Приложение работает не под service account MinIO.</summary>
    NotServiceAccount,

    /// <summary>Приложение уже перешло на новую учётку, но старую не отозвали — сначала отзыв.</summary>
    OldNotRevoked,

    /// <summary>Отзываемая роль — та, под которой приложение работает прямо сейчас.</summary>
    InUse,

    /// <summary>Отзывать нечего: нет активированной ротации, после которой осталась старая учётка.</summary>
    NothingToRevoke,

    /// <summary>Есть выпущенная, но ещё не активированная учётка — отзывать старую рано.</summary>
    PendingRotation,

    StorageUnavailable,

    StorageRejected,
}

public class CredentialRotationException(CredentialFailure failure, string message) : Exception(message)
{
    public CredentialFailure Failure { get; } = failure;
}

/// <summary>
/// Ротация учёток приложения к Postgres и MinIO из админ-панели (ADR-0011), полуавтоматом:
///
///   1. «Сгенерировать» — сервис выпускает новую учётку в ЗАПАСНОМ слоте (у Postgres — соседняя роль,
///      у MinIO — новый service account) и один раз возвращает строки для PROD_ENV. Секрет нигде не
///      сохраняется: в БД — только метаданные (<see cref="CredentialRotation"/>).
///   2. Оператор кладёт строки в PROD_ENV и деплоит — приложение перезапускается уже под новой учёткой.
///   3. «Отозвать старую» — после того как панель увидела, что приложение работает под новой (это
///      определяется по тому, под чем оно подключено, а не по клику), старая учётка отключается.
///
/// Приложение не может отозвать учётку, под которой работает само (Postgres — на уровне функции,
/// MinIO — здесь), а до перехода на новую отзыв вообще недоступен.
/// </summary>
public class AdminCredentialsService(
    AppDbContext db,
    IDbCredentialAdmin postgres,
    IMinioCredentialAdmin minio,
    ILogger<AdminCredentialsService> logger)
{
    private const int HistoryLimit = 20;

    // ─── Состояние ──────────────────────────────────────────────────────────────────────────────

    public async Task<CredentialsStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var pg = await GetPostgresStatusAsync(ct);
        var mn = await GetMinioStatusAsync(ct);
        var history = await db.CredentialRotations.AsNoTracking()
            .OrderByDescending(r => r.GeneratedAt).Take(HistoryLimit).ToListAsync(ct);
        return new CredentialsStatusDto(pg, mn, history.Select(ToDto).ToList());
    }

    private async Task<PostgresCredentialStatusDto> GetPostgresStatusAsync(CancellationToken ct)
    {
        DbSessionInfo session;
        try
        {
            session = await postgres.GetSessionAsync(ct);
        }
        catch (CredentialAdminException)
        {
            return new PostgresCredentialStatusDto("Unavailable", null, [], null, null, null);
        }

        if (!session.RotationFunctionsPresent || !DbAppRoles.IsAppSlot(session.SessionUser))
            return new PostgresCredentialStatusDto("Superuser", session.SessionUser, [], null, null, null);

        var slots = await Guard(() => postgres.GetSlotsAsync(ct));
        var rows = await LoadRowsAsync(CredentialKind.Postgres, ct);
        await ActivatePendingIfCurrentAsync(rows, session.SessionUser, ct);

        return new PostgresCredentialStatusDto(
            "LeastPrivilege", session.SessionUser,
            slots.Select(s => new CredentialSlotDto(s.RoleName, s.CanLogin, s.HasPassword, s.ActiveSessions)).ToList(),
            CurrentSince(rows, session.SessionUser),
            Pending(rows) is { } p ? ToDto(p) : null,
            ActivatedFor(rows, session.SessionUser)?.FromIdentity);
    }

    private async Task<MinioCredentialStatusDto> GetMinioStatusAsync(CancellationToken ct)
    {
        var current = minio.CurrentAccessKey;
        var mode = await minio.GetModeAsync(ct);
        var modeName = mode.ToString();

        if (mode != MinioCredentialMode.ServiceAccount)
            return new MinioCredentialStatusDto(modeName, Mask(current), null, null, null);

        var rows = await LoadRowsAsync(CredentialKind.Minio, ct);
        await ActivatePendingIfCurrentAsync(rows, current, ct);

        return new MinioCredentialStatusDto(
            modeName, Mask(current), CurrentSince(rows, current),
            Pending(rows) is { } p ? ToDto(p) : null,
            ActivatedFor(rows, current) is { } a ? Mask(a.FromIdentity) : null);
    }

    // ─── Postgres ───────────────────────────────────────────────────────────────────────────────

    public async Task<GeneratedCredentialDto> GeneratePostgresAsync(string admin, CancellationToken ct = default)
    {
        var current = await RequireLeastPrivilegeAsync(ct);
        var rows = await LoadRowsAsync(CredentialKind.Postgres, ct);
        await ActivatePendingIfCurrentAsync(rows, current, ct);
        if (ActivatedFor(rows, current) is not null)
            throw new CredentialRotationException(CredentialFailure.OldNotRevoked, "Сначала отзовите старую учётку.");

        var target = DbAppRoles.Sibling(current);
        var password = SecretGenerator.PostgresPassword();
        await Guard(async () =>
        {
            await postgres.SetPasswordAsync(target, ScramSha256Verifier.Create(password), ct);
            return 0;
        });

        var row = await ReplacePendingAsync(rows, CredentialKind.Postgres, current, target, admin, ct);
        Audit("generate", row, admin);
        return new GeneratedCredentialDto(row.Id, [$"DB_APP_USER={target}", $"DB_APP_PASSWORD={password}"]);
    }

    public async Task<RevokedCredentialDto> RevokePostgresOldAsync(string admin, CancellationToken ct = default)
    {
        var current = await RequireLeastPrivilegeAsync(ct);
        var rows = await LoadRowsAsync(CredentialKind.Postgres, ct);
        await ActivatePendingIfCurrentAsync(rows, current, ct);
        var row = RequireRevocable(rows, current);

        var terminated = await Guard(() => postgres.DisableAsync(row.FromIdentity, ct));

        row.Status = CredentialRotationStatus.Revoked;
        row.RevokedAt = DateTime.UtcNow;
        row.RevokedBy = admin;
        row.TerminatedSessions = terminated;
        await db.SaveChangesAsync(ct);
        Audit("revoke", row, admin);
        return new RevokedCredentialDto(terminated);
    }

    private async Task<string> RequireLeastPrivilegeAsync(CancellationToken ct)
    {
        var session = await Guard(() => postgres.GetSessionAsync(ct));
        if (!session.RotationFunctionsPresent || !DbAppRoles.IsAppSlot(session.SessionUser))
            throw new CredentialRotationException(CredentialFailure.NotLeastPrivilege,
                "Приложение работает не под учётками familyhub_app_a/b — выполните первичную настройку (deploy/README.md).");
        return session.SessionUser;
    }

    // ─── MinIO ──────────────────────────────────────────────────────────────────────────────────

    public async Task<GeneratedCredentialDto> GenerateMinioAsync(string admin, CancellationToken ct = default)
    {
        var current = await RequireServiceAccountAsync(ct);
        var rows = await LoadRowsAsync(CredentialKind.Minio, ct);
        await ActivatePendingIfCurrentAsync(rows, current, ct);
        if (ActivatedFor(rows, current) is not null)
            throw new CredentialRotationException(CredentialFailure.OldNotRevoked, "Сначала отзовите старый ключ.");

        // Прежний, так и не активированный ключ (секрет потеряли и генерируют заново) больше не нужен —
        // удаляем, чтобы в MinIO не копились действующие ключи, о которых никто не знает.
        if (Pending(rows) is { } stale)
            await Guard(async () => { await minio.DeleteServiceAccountAsync(stale.ToIdentity, ct); return 0; });

        var accessKey = SecretGenerator.MinioAccessKey();
        var secretKey = SecretGenerator.MinioSecretKey();
        await Guard(async () =>
        {
            await minio.CreateServiceAccountAsync(accessKey, secretKey, $"familyhub-app-{DateTime.UtcNow:yyyyMMdd-HHmm}", ct);
            return 0;
        });

        var row = await ReplacePendingAsync(rows, CredentialKind.Minio, current, accessKey, admin, ct);
        Audit("generate", row, admin);
        return new GeneratedCredentialDto(row.Id, [$"MINIO_APP_ACCESS_KEY={accessKey}", $"MINIO_APP_SECRET_KEY={secretKey}"]);
    }

    public async Task<RevokedCredentialDto> RevokeMinioOldAsync(string admin, CancellationToken ct = default)
    {
        var current = await RequireServiceAccountAsync(ct);
        var rows = await LoadRowsAsync(CredentialKind.Minio, ct);
        await ActivatePendingIfCurrentAsync(rows, current, ct);
        var row = RequireRevocable(rows, current);

        await Guard(async () => { await minio.DeleteServiceAccountAsync(row.FromIdentity, ct); return 0; });

        row.Status = CredentialRotationStatus.Revoked;
        row.RevokedAt = DateTime.UtcNow;
        row.RevokedBy = admin;
        await db.SaveChangesAsync(ct);
        Audit("revoke", row, admin);
        return new RevokedCredentialDto(null);
    }

    private async Task<string> RequireServiceAccountAsync(CancellationToken ct)
    {
        var mode = await minio.GetModeAsync(ct);
        return mode switch
        {
            MinioCredentialMode.ServiceAccount => minio.CurrentAccessKey,
            MinioCredentialMode.Unknown => throw new CredentialRotationException(CredentialFailure.StorageUnavailable, "MinIO недоступен."),
            _ => throw new CredentialRotationException(CredentialFailure.NotServiceAccount,
                "Приложение работает не под service account MinIO — выполните первичную настройку (deploy/README.md)."),
        };
    }

    // ─── Общее: жизненный цикл ротации ──────────────────────────────────────────────────────────

    private Task<List<CredentialRotation>> LoadRowsAsync(CredentialKind kind, CancellationToken ct) =>
        db.CredentialRotations.Where(r => r.Kind == kind).OrderBy(r => r.GeneratedAt).ToListAsync(ct);

    private static CredentialRotation? Pending(List<CredentialRotation> rows) =>
        rows.LastOrDefault(r => r.Status == CredentialRotationStatus.AwaitingDeploy);

    /// <summary>Ротация, после которой приложение работает под <paramref name="current"/>, а старая
    /// учётка (<c>FromIdentity</c>) ещё не отозвана. Запись Activated с ДРУГОЙ ToIdentity — «хвост» после
    /// отката приложения на прежнюю учётку — сюда не относится.</summary>
    private static CredentialRotation? ActivatedFor(List<CredentialRotation> rows, string current) =>
        rows.LastOrDefault(r => r.Status == CredentialRotationStatus.Activated && r.ToIdentity == current);

    private static DateTime? CurrentSince(List<CredentialRotation> rows, string current) =>
        rows.Where(r => r.ToIdentity == current && r.ActivatedAt != null).Max(r => r.ActivatedAt);

    /// <summary>Приложение подключилось под выпущенной учёткой → ротация «активирована». Определяется
    /// по фактическому подключению (session_user / Minio:AccessKey), а не по действию человека.</summary>
    private async Task ActivatePendingIfCurrentAsync(List<CredentialRotation> rows, string current, CancellationToken ct)
    {
        if (Pending(rows) is not { } pending || pending.ToIdentity != current) return;

        pending.Status = CredentialRotationStatus.Activated;
        pending.ActivatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "AdminAudit: credentials activated kind={Kind} from={From} to={To} (приложение работает под новой учёткой)",
            pending.Kind, pending.FromIdentity, pending.ToIdentity);
    }

    private static CredentialRotation RequireRevocable(List<CredentialRotation> rows, string current)
    {
        if (Pending(rows) is not null)
            throw new CredentialRotationException(CredentialFailure.PendingRotation,
                "Есть выпущенная, но ещё не применённая учётка — сначала задеплойте её.");

        var row = ActivatedFor(rows, current)
            ?? throw new CredentialRotationException(CredentialFailure.NothingToRevoke, "Нет старой учётки, которую можно отозвать.");

        // Страховка: по построению FromIdentity != current, но отозвать учётку, под которой работает
        // приложение, нельзя ни при каких данных в таблице.
        if (row.FromIdentity == current)
            throw new CredentialRotationException(CredentialFailure.InUse, "Нельзя отозвать учётку, под которой приложение работает сейчас.");

        return row;
    }

    private async Task<CredentialRotation> ReplacePendingAsync(
        List<CredentialRotation> rows, CredentialKind kind, string from, string to, string admin, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Сначала снимаем прежнюю «ждёт деплоя» отдельным SaveChanges: уникальный частичный индекс
        // (Kind WHERE Status=AwaitingDeploy) не терпит двух таких строк даже внутри одного батча.
        foreach (var previous in rows.Where(r => r.Status == CredentialRotationStatus.AwaitingDeploy))
            previous.Status = CredentialRotationStatus.Superseded;
        await db.SaveChangesAsync(ct);

        var row = new CredentialRotation
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            FromIdentity = from,
            ToIdentity = to,
            Status = CredentialRotationStatus.AwaitingDeploy,
            GeneratedAt = DateTime.UtcNow,
            GeneratedBy = admin,
        };
        db.CredentialRotations.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    // ─── Вспомогательное ────────────────────────────────────────────────────────────────────────

    /// <summary>Аудит: кто, что и над какой учёткой. В лог идут только идентификаторы (роль / access key),
    /// но не пароль и не secret key.</summary>
    private void Audit(string action, CredentialRotation row, string admin) =>
        logger.LogWarning("AdminAudit: credentials {Action} kind={Kind} from={From} to={To} by={Admin}",
            action, row.Kind, row.FromIdentity, row.ToIdentity, admin);

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (CredentialAdminException e)
        {
            throw e.Error switch
            {
                CredentialAdminError.InUse => new CredentialRotationException(CredentialFailure.InUse, e.Message),
                CredentialAdminError.Unavailable => new CredentialRotationException(CredentialFailure.StorageUnavailable, e.Message),
                _ => new CredentialRotationException(CredentialFailure.StorageRejected, e.Message),
            };
        }
    }

    /// <summary>Маска: первые и последние 4 символа — по ней видно, какой ключ в работе, но целиком он
    /// в интерфейс не уходит.</summary>
    public static string Mask(string identity) =>
        identity.Length < 12 ? $"{identity[..Math.Min(2, identity.Length)]}…" : $"{identity[..4]}…{identity[^4..]}";

    private static CredentialRotationDto ToDto(CredentialRotation r) => new(
        r.Id, r.Kind.ToString(), Display(r.Kind, r.FromIdentity), Display(r.Kind, r.ToIdentity), r.Status.ToString(),
        r.GeneratedAt, r.GeneratedBy, r.ActivatedAt, r.RevokedAt, r.TerminatedSessions);

    // Имя роли Postgres — не секрет, показываем как есть; access key MinIO — в маске.
    private static string Display(CredentialKind kind, string identity) => kind == CredentialKind.Minio ? Mask(identity) : identity;
}
