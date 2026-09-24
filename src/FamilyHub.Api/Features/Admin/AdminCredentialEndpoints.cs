namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// /api/admin/credentials* — ротация учёток приложения к Postgres/MinIO (ADR-0011, см.
/// AdminCredentialsService). Группа наследует RequireAuthorization("PlatformAdmin") от /api/admin.
/// Ответы «Сгенерировать» несут секрет — не должны кэшироваться (NoStoreMiddleware закрывает весь /api).
/// </summary>
public static class AdminCredentialEndpoints
{
    public static void MapCredentialEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/credentials", async (AdminCredentialsService credentials, CancellationToken ct) =>
            Results.Ok(await credentials.GetStatusAsync(ct)));

        group.MapPost("/credentials/postgres/generate", (HttpContext http, AdminCredentialsService credentials, CancellationToken ct) =>
            Run(async () => Results.Ok(await credentials.GeneratePostgresAsync(AdminName(http), ct))));

        group.MapPost("/credentials/postgres/revoke-old", (HttpContext http, AdminCredentialsService credentials, CancellationToken ct) =>
            Run(async () => Results.Ok(await credentials.RevokePostgresOldAsync(AdminName(http), ct))));

        group.MapPost("/credentials/minio/generate", (HttpContext http, AdminCredentialsService credentials, CancellationToken ct) =>
            Run(async () => Results.Ok(await credentials.GenerateMinioAsync(AdminName(http), ct))));

        group.MapPost("/credentials/minio/revoke-old", (HttpContext http, AdminCredentialsService credentials, CancellationToken ct) =>
            Run(async () => Results.Ok(await credentials.RevokeMinioOldAsync(AdminName(http), ct))));
    }

    private static string AdminName(HttpContext http) => http.User.Identity?.Name ?? "admin";

    /// <summary>Отказ бизнес-правила → 409 с машинным кодом (UI показывает по нему понятный текст);
    /// проблемы самих хранилищ → 503/502. Текст исключения в ответ не уходит: он для логов.</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (CredentialRotationException e)
        {
            var (code, status) = e.Failure switch
            {
                CredentialFailure.NotLeastPrivilege => ("not_least_privilege", StatusCodes.Status409Conflict),
                CredentialFailure.NotServiceAccount => ("not_service_account", StatusCodes.Status409Conflict),
                CredentialFailure.OldNotRevoked => ("old_not_revoked", StatusCodes.Status409Conflict),
                CredentialFailure.InUse => ("in_use", StatusCodes.Status409Conflict),
                CredentialFailure.NothingToRevoke => ("nothing_to_revoke", StatusCodes.Status409Conflict),
                CredentialFailure.PendingRotation => ("pending_rotation", StatusCodes.Status409Conflict),
                CredentialFailure.StorageUnavailable => ("storage_unavailable", StatusCodes.Status503ServiceUnavailable),
                _ => ("storage_rejected", StatusCodes.Status502BadGateway),
            };
            return Results.Json(new { code }, statusCode: status);
        }
    }
}
