using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.MedicationCourses;

public static class MedicationCourseEndpoints
{
    public static void MapMedicationCourseEndpoints(this IEndpointRouteBuilder app)
    {
        var courses = app.MapGroup("/api/medication-courses").RequireAuthorization();

        courses.MapGet("", async (
            string? status, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(currentUser.UserId, completed: status == "completed", ct)));

        // «Сегодня»: subject — all | me | u:{id} | d:{id}.
        courses.MapGet("/today", async (
            DateOnly? date, string? subject, MedicationTodayService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetTodayAsync(currentUser.UserId, date, subject, ct)));

        courses.MapGet("/attention-count", async (
            MedicationTodayService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(new AttentionCountResponse(await service.GetAttentionCountAsync(currentUser.UserId, ct))));

        courses.MapGet("/prescriptions", async (
            Guid? dependentId, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetPrescriptionsAsync(currentUser.UserId, dependentId, ct);
            return result == CourseResult.NotFound ? Results.NotFound() : Results.Ok(items);
        });

        courses.MapPost("/preview", async (
            CoursePreviewRequest request, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.PreviewAsync(currentUser.UserId, request, ct);
            return result == CourseResult.Invalid ? Results.BadRequest(new { message = error }) : Results.Ok(item);
        });

        courses.MapGet("/{courseId:guid}", async (
            Guid courseId, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetAsync(currentUser.UserId, courseId, ct);
            return result == CourseResult.NotFound ? Results.NotFound() : Results.Ok(item);
        });

        courses.MapGet("/{courseId:guid}/history", async (
            Guid courseId, int? weeks, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetHistoryAsync(currentUser.UserId, courseId, weeks ?? 2, ct);
            return result == CourseResult.NotFound ? Results.NotFound() : Results.Ok(item);
        });

        courses.MapPost("", async (
            CourseRequest request, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.CreateAsync(currentUser.UserId, request, ct);
            return result switch
            {
                CourseResult.NotFound => Results.NotFound(),
                CourseResult.Invalid => Results.BadRequest(new { message = error }),
                _ => Results.Created($"/api/medication-courses/{item!.Summary.Id}", item),
            };
        }).RequireRateLimiting("medical-write");

        courses.MapPut("/{courseId:guid}", async (
            Guid courseId, CourseRequest request, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.UpdateAsync(currentUser.UserId, courseId, request, ct);
            return Map(result, error, item);
        }).RequireRateLimiting("medical-write");

        courses.MapPost("/{courseId:guid}/pause", async (
            Guid courseId, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.PauseAsync(currentUser.UserId, courseId, ct))).RequireRateLimiting("medical-write");

        courses.MapPost("/{courseId:guid}/resume", async (
            Guid courseId, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.ResumeAsync(currentUser.UserId, courseId, ct))).RequireRateLimiting("medical-write");

        courses.MapPost("/{courseId:guid}/complete", async (
            Guid courseId, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.CompleteAsync(currentUser.UserId, courseId, ct))).RequireRateLimiting("medical-write");

        courses.MapDelete("/{courseId:guid}", async (
            Guid courseId, MedicationCourseService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.DeleteAsync(currentUser.UserId, courseId, ct))).RequireRateLimiting("medical-write");

        courses.MapPost("/{courseId:guid}/doses", async (
            Guid courseId, DoseActionRequest request, DoseService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.ApplyAsync(
                currentUser.UserId, courseId, request.ScheduledAt, request.Action, request.TakenAt, ct);
            return Map(result, error, item);
        }).RequireRateLimiting("medical-write");

        courses.MapPost("/{courseId:guid}/prn", async (
            Guid courseId, PrnRequest request, DoseService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.TakeAsNeededAsync(currentUser.UserId, courseId, request, ct);
            return Map(result, error, item);
        }).RequireRateLimiting("medical-write");

        // Отмена отметки: вернуть таблетки в аптечку и убрать запись дневника.
        app.MapDelete("/api/medication-doses/{doseId:guid}/action", async (
            Guid doseId, DoseService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.UndoAsync(currentUser.UserId, doseId, ct)))
            .RequireAuthorization().RequireRateLimiting("medical-write");

        var reminders = app.MapGroup("/api/medication-reminders").RequireAuthorization();

        reminders.MapGet("/settings", async (
            MedicationReminderSettingsService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(currentUser.UserId, ct)));

        reminders.MapPut("/my-watchers", async (
            SetMyWatchersRequest request, MedicationReminderSettingsService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.SetMyWatchersAsync(currentUser.UserId, request, ct))).RequireRateLimiting("medical-write");

        reminders.MapPut("/watching/{kind}/{id:guid}", async (
            string kind, Guid id, SetWatchingRequest request, MedicationReminderSettingsService service,
            ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.SetWatchingAsync(currentUser.UserId, kind, id, request, ct))).RequireRateLimiting("medical-write");

        reminders.MapPut("/quiet-hours", async (
            QuietHoursRequest request, MedicationReminderSettingsService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.SetQuietHoursAsync(currentUser.UserId, request, ct))).RequireRateLimiting("medical-write");
    }

    private static IResult Map(CourseResult result) => result switch
    {
        CourseResult.Success => Results.NoContent(),
        CourseResult.NotFound => Results.NotFound(),
        CourseResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.Conflict(new { message = "Действие недоступно для этого курса." }),
    };

    private static IResult Map(CourseResult result, string? error, object? item) => result switch
    {
        CourseResult.Success => Results.Ok(item),
        CourseResult.NotFound => Results.NotFound(),
        CourseResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.BadRequest(new { message = error }),
    };

    private static IResult Map(DoseResult result) => result switch
    {
        DoseResult.Success => Results.NoContent(),
        DoseResult.NotFound => Results.NotFound(),
        DoseResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.Conflict(new { message = "Действие недоступно для этого приёма." }),
    };

    internal static IResult Map(DoseResult result, string? error, object? item) => result switch
    {
        DoseResult.Success => Results.Ok(item),
        DoseResult.NotFound => Results.NotFound(),
        DoseResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        DoseResult.Invalid => Results.BadRequest(new { message = error }),
        DoseResult.OverLimit => Results.Conflict(new { message = error, code = "over_limit" }),
        _ => Results.Conflict(new { message = error }),
    };

    private static IResult Map(ReminderSettingsResult result) => result switch
    {
        ReminderSettingsResult.Success => Results.NoContent(),
        ReminderSettingsResult.NotFound => Results.NotFound(),
        _ => Results.BadRequest(new { message = "Некорректные настройки." }),
    };
}
