using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Ручная правка справочников после ИИ из админки (§3 плана) — показатели, медикаменты,
/// источники. Каждое поле, присланное в PUT-теле показателя/медикамента, автоматически лочится
/// (AdminCatalogService) — следующее автообогащение его не тронет. Поиск/листинг переиспользует
/// существующие публичные сервисы (KbAnalyteCatalogService/KbCatalogService/GlobalSpecimenKbService) —
/// та же выдача, что видит обычный пользователь в разделе «Справочник», только с добавленными
/// кнопками правки.
/// </summary>
public static class AdminCatalogEndpoints
{
    /// <summary>Правка статьи (синонимы, биоматериал, объединение) могла сделать находимыми показатели, чьи
    /// платные поиски ждут в «Одобрении», — LabAnalyteKbRekeyJob заодно приведёт ключ строки к текущему
    /// нормализатору и закроет такие поиски. Фоном: ответ на правку не ждёт обхода справочника.</summary>
    private static void RecheckParkedSearches(IBackgroundJobClient jobs) =>
        jobs.Enqueue<LabAnalyteKbRekeyJob>(j => j.RunAsync(CancellationToken.None));

    public static void MapAdminCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/kb").RequireAuthorization("PlatformAdmin");

        // --- Показатели ---

        // Админский список (ADR-0018): те же поля, что у публичного, плюс внутренний статус проверки и фильтр
        // verification=all|unverified|verified. Пользовательские DTO статус не содержат (KbVerificationNotExposedTests).
        group.MapGet("/lab-analytes", async (
            string? q, int? skip, int? take, string? verification, AdminCatalogService admin, CancellationToken ct) =>
            Results.Ok(await admin.SearchLabAnalytesAsync(q, ParseVerificationFilter(verification), skip ?? 0, take ?? 20, ct)));

        // Отметить проверенным без правок («Отметить проверенным»); status — необязательный: по умолчанию AdminVerified.
        group.MapPost("/lab-analytes/{id:guid}/verify", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
            await admin.MarkVerifiedAsync(KbChangeTarget.LabAnalyteKb, id, KbVerificationStatus.AdminVerified, "Отмечено проверенным", ct)
                ? Results.NoContent() : Results.NotFound());

        group.MapGet("/verification-summary", async (AdminCatalogService admin, CancellationToken ct) =>
            Results.Ok(await admin.GetVerificationSummaryAsync(ct)));

        group.MapGet("/lab-analytes/{id:guid}", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
        {
            var detail = await admin.GetLabAnalyteAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPut("/lab-analytes/{id:guid}", async (
            Guid id, AdminKbEditRequest request, AdminCatalogService admin, IBackgroundJobClient jobs, CancellationToken ct) =>
        {
            var (result, detail, reason) = await admin.UpdateLabAnalyteAsync(id, request, ct);
            if (result == AdminKbEditResult.Ok) RecheckParkedSearches(jobs);
            return result switch
            {
                AdminKbEditResult.Ok => Results.Ok(detail),
                AdminKbEditResult.InvalidPayloadJson => Results.BadRequest(new { message = "PayloadJson — невалидный JSON." }),
                AdminKbEditResult.IsolationViolation => Results.BadRequest(new { message = $"Подозрение на персональный контекст: {reason}" }),
                _ => Results.NotFound(),
            };
        });

        group.MapPut("/lab-analytes/{id:guid}/specimen", async (
            Guid id, AdminChangeSpecimenRequest request, AdminCatalogService admin, IBackgroundJobClient jobs, CancellationToken ct) =>
        {
            var (result, detail, conflict) = await admin.ChangeLabAnalyteSpecimenAsync(id, request.SpecimenKbId, ct);
            if (result == AdminSpecimenChangeResult.Ok) RecheckParkedSearches(jobs);
            return result switch
            {
                AdminSpecimenChangeResult.Ok => Results.Ok(detail),
                AdminSpecimenChangeResult.Conflict => Results.Json(
                    new
                    {
                        code = "exists", existingId = conflict!.ExistingId, existingDisplayName = conflict.ExistingDisplayName,
                        message = $"Для этого биоматериала уже есть статья «{conflict.ExistingDisplayName}».",
                    },
                    statusCode: StatusCodes.Status409Conflict),
                AdminSpecimenChangeResult.SpecimenNotFound => Results.BadRequest(new { code = "specimen_not_found", message = "Нет такого биоматериала." }),
                _ => Results.NotFound(),
            };
        });

        group.MapDelete("/lab-analytes/{id:guid}/locks/{field}", async (
            Guid id, string field, AdminCatalogService admin, CancellationToken ct) =>
            await admin.UnlockLabAnalyteFieldAsync(id, field, ct) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/lab-analytes/{id:guid}", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
            await admin.DeleteLabAnalyteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Резолв имён «Что смотрят вместе» в реальные строки справочника (пикер в форме payload-
        // редактора) — POST с телом-списком, не GET с query-строкой: список имён может быть
        // длинным/содержать спецсимволы, тот же выбор, что у bulk-операций в остальной админке.
        group.MapPost("/lab-analytes/resolve-related", async (
            List<string> names, AdminCatalogService admin, CancellationToken ct) =>
            Results.Ok(await admin.ResolveRelatedNamesAsync(names, ct)));

        group.MapPost("/lab-analytes/{loserId:guid}/merge-into/{winnerId:guid}", async (
            Guid loserId, Guid winnerId, AdminCatalogService admin, IBackgroundJobClient jobs, CancellationToken ct) =>
        {
            var result = await admin.MergeLabAnalytesAsync(loserId, winnerId, ct);
            if (result == AdminKbMergeResult.Ok) RecheckParkedSearches(jobs);
            return result switch
            {
                AdminKbMergeResult.Ok => Results.NoContent(),
                AdminKbMergeResult.SameId => Results.Json(
                    new { code = "same_id", message = "Победитель и проигравший — одна и та же строка." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        // --- Медикаменты ---

        group.MapGet("/medications", async (
            string? q, int? skip, int? take, string? verification, AdminCatalogService admin, CancellationToken ct) =>
            Results.Ok(await admin.SearchMedicationsAsync(q, ParseVerificationFilter(verification), skip ?? 0, take ?? 20, ct)));

        group.MapPost("/medications/{id:guid}/verify", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
            await admin.MarkVerifiedAsync(KbChangeTarget.MedicationKb, id, KbVerificationStatus.AdminVerified, "Отмечено проверенным", ct)
                ? Results.NoContent() : Results.NotFound());

        group.MapGet("/medications/{id:guid}", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
        {
            var detail = await admin.GetMedicationAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPost("/medications/{loserId:guid}/merge-into/{winnerId:guid}", async (
            Guid loserId, Guid winnerId, AdminCatalogService admin, CancellationToken ct) =>
        {
            var result = await admin.MergeMedicationsAsync(loserId, winnerId, ct);
            return result switch
            {
                AdminKbMergeResult.Ok => Results.NoContent(),
                AdminKbMergeResult.SameId => Results.Json(
                    new { code = "same_id", message = "Победитель и проигравший — одна и та же строка." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        group.MapPut("/medications/{id:guid}", async (
            Guid id, AdminKbEditRequest request, AdminCatalogService admin, CancellationToken ct) =>
        {
            var (result, detail, reason) = await admin.UpdateMedicationAsync(id, request, ct);
            return result switch
            {
                AdminKbEditResult.Ok => Results.Ok(detail),
                AdminKbEditResult.InvalidPayloadJson => Results.BadRequest(new { message = "PayloadJson — невалидный JSON." }),
                AdminKbEditResult.IsolationViolation => Results.BadRequest(new { message = $"Подозрение на персональный контекст: {reason}" }),
                _ => Results.NotFound(),
            };
        });

        group.MapDelete("/medications/{id:guid}/locks/{field}", async (
            Guid id, string field, AdminCatalogService admin, CancellationToken ct) =>
            await admin.UnlockMedicationFieldAsync(id, field, ct) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/medications/{id:guid}", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
            await admin.DeleteMedicationAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // --- Источники показателей ---

        // Группы поиска биоматериалов (ADR-0018): биоматериалы с одной группой делят кэш и запрос платного поиска.
        group.MapGet("/specimens/search-groups", async (string? q, int? take, SpecimenSearchGroupService groups, CancellationToken ct) =>
            Results.Ok(await groups.ListAsync(q, take ?? 100, ct)));

        group.MapPut("/specimens/{id:guid}/search-group", async (
            Guid id, SetSearchGroupRequest request, SpecimenSearchGroupService groups, CancellationToken ct) =>
        {
            var outcome = await groups.SetGroupAsync(id, request.SearchGroupKey, ct);
            return outcome.Result switch
            {
                SetSearchGroupResult.Ok => Results.Ok(new { mergedRows = outcome.MergedRows, copiedRows = outcome.CopiedRows }),
                SetSearchGroupResult.Invalid => Results.BadRequest(new { code = "invalid", message = outcome.Error }),
                _ => Results.NotFound(),
            };
        });

        group.MapGet("/specimens", async (string? q, int? take, GlobalSpecimenKbService specimens, CancellationToken ct) =>
            Results.Ok(await specimens.SearchAdminAsync(q, take ?? 20, ct)));

        group.MapPut("/specimens/{id:guid}/aliases", async (
            Guid id, AdminSpecimenAliasesRequest request, GlobalSpecimenKbService specimens, CancellationToken ct) =>
        {
            var (result, conflictWith) = await specimens.SetAliasesAsync(id, request.Aliases ?? [], ct);
            return result switch
            {
                SpecimenAliasesResult.Ok => Results.NoContent(),
                SpecimenAliasesResult.Conflict => Results.Json(
                    new { code = "alias_conflict", message = $"Такое название уже принадлежит источнику «{conflictWith}» — объедините их." },
                    statusCode: StatusCodes.Status409Conflict),
                SpecimenAliasesResult.Sentinel => Results.Json(
                    new { code = "sentinel", message = "У системной записи «источник не определён» синонимов нет." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        group.MapPut("/specimens/{id:guid}", async (
            Guid id, AdminSpecimenRenameRequest request, GlobalSpecimenKbService specimens, CancellationToken ct) =>
        {
            var result = await specimens.RenameAsync(id, request.DisplayName, ct);
            return result switch
            {
                SpecimenRenameResult.Ok => Results.NoContent(),
                SpecimenRenameResult.Conflict => Results.Json(
                    new { code = "duplicate_or_invalid", message = "Такое название уже есть в справочнике источников." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        group.MapDelete("/specimens/{id:guid}", async (Guid id, GlobalSpecimenKbService specimens, CancellationToken ct) =>
        {
            var result = await specimens.DeleteAsync(id, ct);
            return result switch
            {
                SpecimenDeleteResult.Ok => Results.NoContent(),
                SpecimenDeleteResult.InUse => Results.Json(
                    new { code = "in_use", message = "Источник используется хотя бы одним показателем/статьёй справочника — сначала перепривяжите их." },
                    statusCode: StatusCodes.Status409Conflict),
                SpecimenDeleteResult.Sentinel => Results.Json(
                    new { code = "sentinel", message = "Системная запись «источник не определён» не удаляется." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        group.MapPost("/specimens/{loserId:guid}/merge-into/{winnerId:guid}", async (
            Guid loserId, Guid winnerId, GlobalSpecimenKbService specimens, CancellationToken ct) =>
        {
            var result = await specimens.MergeAsync(loserId, winnerId, ct);
            return result switch
            {
                SpecimenMergeResult.Ok => Results.NoContent(),
                SpecimenMergeResult.SameId => Results.Json(
                    new { code = "same_id", message = "Победитель и проигравший — одна и та же строка." },
                    statusCode: StatusCodes.Status409Conflict),
                SpecimenMergeResult.Sentinel => Results.Json(
                    new { code = "sentinel", message = "Системная запись «источник не определён» не может быть проигравшей." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });
    }

    private static AdminKbVerificationFilter ParseVerificationFilter(string? value) => value?.ToLowerInvariant() switch
    {
        "unverified" => AdminKbVerificationFilter.Unverified,
        "verified" => AdminKbVerificationFilter.Verified,
        _ => AdminKbVerificationFilter.All,
    };
}

/// <summary>Тело PUT /specimens/{id}/search-group: null/пусто — биоматериал вне групп (свой кэш и запрос).</summary>
public record SetSearchGroupRequest(string? SearchGroupKey);
