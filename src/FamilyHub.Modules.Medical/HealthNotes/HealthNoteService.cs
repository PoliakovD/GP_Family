using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.HealthNotes;

/// <summary>
/// Личный дневник самочувствия. Каждая операция скоупится по OwnerUserId: чужая запись для
/// вызывающего неотличима от несуществующей (NotFound, не Forbidden — не раскрываем существование).
/// Поля зашифрованы, поэтому SQL фильтрует по владельцу/виду/времени, остальное — в памяти.
/// </summary>
public class HealthNoteService(AppDbContext db, ILogger<HealthNoteService> logger)
{
    /// <summary>Потолок выдачи ленты — защита от выгрузки многолетнего дневника одним запросом.</summary>
    public const int MaxListSize = 1000;

    private static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);
    private static readonly DateTime EarliestAllowed = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<List<HealthNoteDto>> ListAsync(
        Guid userId, DateTime? from, DateTime? to, HealthNoteKind? kind, CancellationToken ct = default)
    {
        var query = db.HealthNotes.AsNoTracking().Where(n => n.OwnerUserId == userId);
        if (from is { } f) query = query.Where(n => n.OccurredAt >= AsUtc(f));
        if (to is { } t) query = query.Where(n => n.OccurredAt < AsUtc(t));
        if (kind is { } k) query = query.Where(n => n.Kind == k);

        var rows = await query.OrderByDescending(n => n.OccurredAt).Take(MaxListSize).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<(HealthNoteResult Result, HealthNoteDto? Item, string? Error)> CreateAsync(
        Guid userId, HealthNoteRequest request, CancellationToken ct = default)
    {
        var (content, occurredAt, error) = Prepare(request);
        if (error is not null) return (HealthNoteResult.Invalid, null, error);

        var now = DateTime.UtcNow;
        var note = new HealthNote
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(note, content!, occurredAt, request.IncludeInDoctorQuestions);

        db.HealthNotes.Add(note);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Запись дневника {NoteId} ({Kind}) создана пользователем {UserId}", note.Id, note.Kind, userId);
        return (HealthNoteResult.Success, ToDto(note), null);
    }

    /// <summary>Готовит запись «приём лекарства» без сохранения — для отметки приёма по курсу, где запись
    /// дневника, приём и списание из аптечки должны попасть в одну транзакцию. Вызывающий сам делает
    /// SaveChanges.</summary>
    public HealthNote StageIntake(Guid userId, string drugName, string? dose, DateTime occurredAtUtc)
    {
        var now = DateTime.UtcNow;
        var content = new HealthNoteContent(HealthNoteKind.MedicationIntake, Normalize(drugName), null,
            Intake: new MedicationIntakeData(Normalize(dose)));
        var note = new HealthNote { Id = Guid.NewGuid(), OwnerUserId = userId, CreatedAt = now, UpdatedAt = now };
        Apply(note, content, AsUtc(occurredAtUtc), includeInDoctorQuestions: false);
        db.HealthNotes.Add(note);
        return note;
    }

    public async Task<(HealthNoteResult Result, HealthNoteDto? Item, string? Error)> UpdateAsync(
        Guid userId, Guid noteId, HealthNoteRequest request, CancellationToken ct = default)
    {
        var note = await db.HealthNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.OwnerUserId == userId, ct);
        if (note is null) return (HealthNoteResult.NotFound, null, null);

        var (content, occurredAt, error) = Prepare(request);
        if (error is not null) return (HealthNoteResult.Invalid, null, error);

        Apply(note, content!, occurredAt, request.IncludeInDoctorQuestions);
        note.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return (HealthNoteResult.Success, ToDto(note), null);
    }

    public async Task<HealthNoteResult> DeleteAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        // ExecuteDelete — не трогает [Encrypted]-поля, а свой/чужой различается тем же предикатом.
        var deleted = await db.HealthNotes
            .Where(n => n.Id == noteId && n.OwnerUserId == userId)
            .ExecuteDeleteAsync(ct);
        return deleted == 0 ? HealthNoteResult.NotFound : HealthNoteResult.Success;
    }

    /// <summary>Точки одного замера за период (по возрастанию времени) — для графика и тренда.</summary>
    public async Task<List<HealthMetricPoint>?> GetMetricSeriesAsync(
        Guid userId, string code, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        if (HealthMetricCatalog.Find(code) is null) return null;

        var query = db.HealthNotes.AsNoTracking()
            .Where(n => n.OwnerUserId == userId && n.Kind == HealthNoteKind.Metric);
        if (from is { } f) query = query.Where(n => n.OccurredAt >= AsUtc(f));
        if (to is { } t) query = query.Where(n => n.OccurredAt < AsUtc(t));

        var rows = await query.OrderBy(n => n.OccurredAt).Take(MaxListSize).ToListAsync(ct);
        return rows
            .Select(n => (n.OccurredAt, Metric: HealthNoteRules.ParseContent(n.Kind, n.Title, n.Text, n.DataJson).Metric))
            .Where(x => x.Metric is not null && x.Metric.Code == code)
            .Select(x => new HealthMetricPoint(x.OccurredAt, x.Metric!.Value, x.Metric.Value2))
            .ToList();
    }

    /// <summary>Недавние названия (симптомы/лекарства) для чипов «Недавние:» — уникальные, свежие сверху.</summary>
    public async Task<List<string>> GetRecentTitlesAsync(
        Guid userId, HealthNoteKind kind, int take = 8, CancellationToken ct = default)
    {
        var rows = await db.HealthNotes.AsNoTracking()
            .Where(n => n.OwnerUserId == userId && n.Kind == kind)
            .OrderByDescending(n => n.OccurredAt)
            .Take(200)
            .ToListAsync(ct);

        return rows
            .Select(n => n.Title?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(take)
            .ToList()!;
    }

    private static (HealthNoteContent? Content, DateTime OccurredAt, string? Error) Prepare(HealthNoteRequest r)
    {
        var occurredAt = AsUtc(r.OccurredAt);
        if (occurredAt < EarliestAllowed || occurredAt > DateTime.UtcNow + FutureTolerance)
            return (null, occurredAt, "Время записи указано неверно.");

        // Название осмысленно только у симптома и приёма лекарства; у остальных видов не храним.
        var keepsTitle = r.Kind is HealthNoteKind.Symptom or HealthNoteKind.MedicationIntake;
        var content = new HealthNoteContent(
            r.Kind,
            keepsTitle ? Normalize(r.Title) : null,
            Normalize(r.Text),
            r.Symptom, r.Metric, r.Wellbeing, r.Intake, r.Sleep);

        var error = HealthNoteRules.Validate(content);
        if (error is null && r.Kind == HealthNoteKind.Sleep)
        {
            // Для сна «когда» — момент пробуждения; клиентское значение не должно расходиться с payload.
            occurredAt = AsUtc(r.Sleep!.WakeTime);
        }
        return (error is null ? content : null, occurredAt, error);
    }

    private static void Apply(HealthNote note, HealthNoteContent content, DateTime occurredAt, bool includeInDoctorQuestions)
    {
        note.Kind = content.Kind;
        note.OccurredAt = occurredAt;
        note.Title = content.Title;
        note.Text = content.Text;
        note.DataJson = HealthNoteRules.SerializePayload(content);
        // Флаг «в вопросы к врачу» имеет смысл только у заметки.
        note.IncludeInDoctorQuestions = content.Kind == HealthNoteKind.Note && includeInDoctorQuestions;
    }

    private static HealthNoteDto ToDto(HealthNote n)
    {
        var c = HealthNoteRules.ParseContent(n.Kind, n.Title, n.Text, n.DataJson);
        return new HealthNoteDto(
            n.Id, n.Kind, n.OccurredAt, n.Title, n.Text, n.IncludeInDoctorQuestions,
            c.Symptom, c.Metric, c.Wellbeing, c.Intake, c.Sleep, n.UpdatedAt);
    }

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Npgsql timestamptz принимает только Kind=Utc; Unspecified с клиента трактуем как UTC.</summary>
    private static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };
}
