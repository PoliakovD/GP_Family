using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyHub.Infrastructure.Security.Credentials;

/// <summary>Вызывает функции ротации familyhub_admin.* через соединение приложения (то самое, под
/// ролью familyhub_app_a/b — отдельных прав ему для этого не выдавалось, см. ADR-0011).</summary>
public sealed class NpgsqlDbCredentialAdmin(AppDbContext db) : IDbCredentialAdmin
{
    // Ключи keyless-проекции SqlQuery<T>: имена столбцов результата совпадают с именами свойств.
    private sealed class SlotRow
    {
        public string RoleName { get; set; } = string.Empty;
        public bool CanLogin { get; set; }
        public bool HasPassword { get; set; }
        public int ActiveSessions { get; set; }
    }

    public Task<DbSessionInfo> GetSessionAsync(CancellationToken ct = default) => Translate(async () =>
    {
        var user = (await db.Database.SqlQuery<string>($"SELECT session_user::text AS \"Value\"").ToListAsync(ct)).Single();
        var present = (await db.Database
            .SqlQuery<bool>($"SELECT to_regprocedure('familyhub_admin.app_role_status()') IS NOT NULL AS \"Value\"")
            .ToListAsync(ct)).Single();
        return new DbSessionInfo(user, present);
    });

    public async Task<IReadOnlyList<DbRoleSlot>> GetSlotsAsync(CancellationToken ct = default)
    {
        var rows = await Translate(() => db.Database.SqlQuery<SlotRow>(
            $"SELECT role_name::text AS \"RoleName\", can_login AS \"CanLogin\", has_password AS \"HasPassword\", active_sessions AS \"ActiveSessions\" FROM familyhub_admin.app_role_status()")
            .ToListAsync(ct));
        return rows.Select(r => new DbRoleSlot(r.RoleName, r.CanLogin, r.HasPassword, r.ActiveSessions)).ToList();
    }

    public async Task SetPasswordAsync(string role, string scramVerifier, CancellationToken ct = default)
    {
        await Translate(async () =>
        {
            await db.Database.ExecuteSqlAsync($"SELECT familyhub_admin.set_app_role_password({role}, {scramVerifier})", ct);
            return 0;
        });
    }

    public async Task<int> DisableAsync(string role, CancellationToken ct = default) =>
        (await Translate(() => db.Database
            .SqlQuery<int>($"SELECT familyhub_admin.disable_app_role({role}) AS \"Value\"")
            .ToListAsync(ct))).Single();

    /// <summary>Коды ошибок функций (см. bootstrap-roles.sql) → типизированные исключения. Текст
    /// исходной ошибки Postgres в сообщение не переносится — при ошибке он мог бы содержать параметры.
    ///
    /// EF Core может обернуть PostgresException (для кодов, которые Npgsql считает «временными», в том
    /// числе 55006 — роль в использовании) в InvalidOperationException/RetryLimitExceededException, поэтому
    /// исходную ошибку ищем по всей цепочке InnerException, а не только на верхнем уровне.</summary>
    private static async Task<T> Translate<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (FindPostgresError(e) is { } pg)
        {
            throw pg.SqlState switch
            {
                PostgresErrorCodes.InsufficientPrivilege =>
                    new CredentialAdminException(CredentialAdminError.Forbidden, "Postgres: роль не подлежит ротации из приложения.", pg),
                PostgresErrorCodes.ObjectInUse =>
                    new CredentialAdminException(CredentialAdminError.InUse, "Postgres: роль используется текущим подключением приложения.", pg),
                PostgresErrorCodes.InvalidParameterValue =>
                    new CredentialAdminException(CredentialAdminError.InvalidVerifier, "Postgres: ожидался SCRAM-SHA-256-верификатор.", pg),
                PostgresErrorCodes.UndefinedFunction or PostgresErrorCodes.InvalidSchemaName =>
                    new CredentialAdminException(CredentialAdminError.Rejected, "Postgres: функции ротации не установлены (не выполнена первичная настройка).", pg),
                _ => new CredentialAdminException(CredentialAdminError.Unavailable, "Postgres недоступен или отклонил запрос.", pg),
            };
        }
        catch (NpgsqlException e)
        {
            throw new CredentialAdminException(CredentialAdminError.Unavailable, "Postgres недоступен.", e);
        }
    }

    private static PostgresException? FindPostgresError(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is PostgresException pg) return pg;
        return null;
    }
}
