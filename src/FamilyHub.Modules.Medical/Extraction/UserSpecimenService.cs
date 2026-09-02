using System.Text.RegularExpressions;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

public enum CreateSpecimenResult { Success, AlreadyExists, Rejected, Unavailable, InvalidInput }

/// <summary>Источник в автоподсказке пользователя — SpecimenKbId адресует общий справочник
/// напрямую (пересборка enrich-пайплайна: раньше отдавался Id персональной копии-строки, теперь
/// один источник истины написания — GlobalSpecimenKb, эта таблица только про недавнее
/// использование, см. class doc UserSpecimenService).</summary>
public record UserSpecimenDto(Guid SpecimenKbId, string DisplayName, DateTime LastUsedAt);

/// <summary>
/// "Недавно использованные этим пользователем" источники показателя (UX-редизайн, пересборка
/// enrich-пайплайна) — что автоподсказка (GET /api/specimens) должна предложить В ПЕРВУЮ ОЧЕРЕДЬ
/// конкретному человеку. LLM-гейт для НОВОГО, ещё не встречавшегося названия вынесен в общий
/// <see cref="GlobalSpecimenKbService"/> — то же название, once провалидированное кем-то одним,
/// находится последующими пользователями без нового LLM-вызова.
/// </summary>
public class UserSpecimenService(
    AppDbContext db, GlobalSpecimenKbService globalKb, ILogger<UserSpecimenService> logger)
{
    /// <summary>Буквы (кириллица/латиница), пробел, дефис, скобки — рамки ДО вызова модели,
    /// отсекают явный мусор (числа, спецсимволы, эмодзи) бесплатно.</summary>
    private static readonly Regex ValidCharsRegex = new(@"^[\p{L}\s\-()]+$", RegexOptions.Compiled);

    public async Task<List<UserSpecimenDto>> GetOwnAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var rows = await (
            from us in db.UserSpecimens.AsNoTracking()
            join gsk in db.GlobalSpecimensKb.AsNoTracking() on us.SpecimenKbId equals gsk.Id
            where us.OwnerUserId == ownerUserId
            orderby us.LastUsedAt descending
            select new UserSpecimenDto(gsk.Id, gsk.DisplayName, us.LastUsedAt)
        ).ToListAsync(ct);
        return rows;
    }

    public async Task<(CreateSpecimenResult Result, UserSpecimenDto? Item, string? Reason)> CreateAsync(
        Guid ownerUserId, string? rawName, CancellationToken ct = default)
    {
        var trimmed = rawName?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 60 || !ValidCharsRegex.IsMatch(trimmed))
            return (CreateSpecimenResult.InvalidInput, null,
                "Название должно быть от 2 до 60 символов: буквы, пробел, дефис или скобки.");

        var normalized = LabAnalyteNormalizer.Normalize(trimmed);
        if (normalized.Length == 0)
            return (CreateSpecimenResult.InvalidInput, null, "Не удалось разобрать название.");

        // Кто-то уже провалидировал ровно это название (в т.ч. засеянные общие источники —
        // кровь/моча/кал/... — они обычные строки того же справочника) — переиспользуем его
        // написание бесплатно, без нового LLM-вызова (см. class doc GlobalSpecimenKbService).
        Guid specimenKbId;
        string displayName;
        var globalHit = await globalKb.FindAsync(normalized, ct);
        if (globalHit is not null)
        {
            specimenKbId = globalHit.Id;
            displayName = globalHit.DisplayName;
        }
        else
        {
            var (result, id, validatedName, reason) = await globalKb.ValidateAndRegisterAsync(trimmed, normalized, ct);
            if (result != SpecimenValidationResult.Valid || id is null)
            {
                logger.LogInformation(
                    "Источник «{Name}» ({UserId}): {Result} — {Reason}", trimmed, ownerUserId, result, reason);
                return (result == SpecimenValidationResult.Unavailable
                    ? CreateSpecimenResult.Unavailable
                    : CreateSpecimenResult.Rejected, null, reason);
            }
            specimenKbId = id.Value;
            displayName = validatedName!;
        }

        var now = DateTime.UtcNow;
        var existing = await db.UserSpecimens
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId && s.SpecimenKbId == specimenKbId, ct);
        if (existing is not null)
        {
            existing.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
            return (CreateSpecimenResult.AlreadyExists, new UserSpecimenDto(specimenKbId, displayName, now), null);
        }

        var usage = new UserSpecimen
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            SpecimenKbId = specimenKbId,
            LastUsedAt = now,
        };

        try
        {
            db.UserSpecimens.Add(usage);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка двух одновременных запросов с одним и тем же источником — уникальный индекс
            // (OwnerUserId, SpecimenKbId) поймал то, что проверка выше не успела увидеть.
            var raced = await db.UserSpecimens
                .FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId && s.SpecimenKbId == specimenKbId, ct);
            return raced is null
                ? (CreateSpecimenResult.Unavailable, null, null)
                : (CreateSpecimenResult.AlreadyExists, new UserSpecimenDto(specimenKbId, displayName, raced.LastUsedAt), null);
        }

        logger.LogInformation("Источник «{Name}» добавлен пользователем {UserId}.", displayName, ownerUserId);
        return (CreateSpecimenResult.Success, new UserSpecimenDto(specimenKbId, displayName, now), null);
    }
}
