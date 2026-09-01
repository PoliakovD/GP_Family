using System.Text.RegularExpressions;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

public enum CreateSpecimenResult { Success, AlreadyExists, Rejected, Unavailable, InvalidInput }

/// <summary>
/// Справочник биоматериалов на пользователя (UX-редизайн) — когда фиксированного
/// <see cref="Domain.Enums.SpecimenType"/> не хватает ("ликвор", "мокрота" и т.п.). LLM-гейт
/// (пересборка enrich-пайплайна) вынесен в общий <see cref="GlobalSpecimenKbService"/> — то же
/// название, once провалидированное кем-то одним, находится ПОСЛЕДУЮЩИМИ пользователями без
/// нового LLM-вызова (раньше каждый провалидировал бы одно и то же слово себе заново). Личная
/// запись (эта таблица) остаётся — она то, что реально предлагается автоподсказкой (GET
/// /api/specimens) КОНКРЕТНОМУ пользователю.
/// </summary>
public class UserSpecimenService(
    AppDbContext db, GlobalSpecimenKbService globalKb, ILogger<UserSpecimenService> logger)
{
    /// <summary>Буквы (кириллица/латиница), пробел, дефис, скобки — рамки ДО вызова модели,
    /// отсекают явный мусор (числа, спецсимволы, эмодзи) бесплатно.</summary>
    private static readonly Regex ValidCharsRegex = new(@"^[\p{L}\s\-()]+$", RegexOptions.Compiled);

    /// <summary>Русские подписи системного SpecimenType (см. shared/util/specimen.ts на фронте) —
    /// если пользователь ввёл то же самое своими словами, справочник не нужен вовсе, KB-вызов
    /// экономится.</summary>
    private static readonly HashSet<string> SystemSpecimenNames = new(StringComparer.Ordinal)
    {
        LabAnalyteNormalizer.Normalize("кровь"),
        LabAnalyteNormalizer.Normalize("моча"),
        LabAnalyteNormalizer.Normalize("кал"),
        LabAnalyteNormalizer.Normalize("вагинальный мазок"),
        LabAnalyteNormalizer.Normalize("мазок"),
        LabAnalyteNormalizer.Normalize("слюна"),
        LabAnalyteNormalizer.Normalize("другое"),
        LabAnalyteNormalizer.Normalize("не указано"),
    };

    public async Task<List<UserSpecimen>> GetOwnAsync(Guid ownerUserId, CancellationToken ct = default) =>
        await db.UserSpecimens.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .OrderBy(s => s.DisplayName)
            .ToListAsync(ct);

    public async Task<(CreateSpecimenResult Result, UserSpecimen? Item, string? Reason)> CreateAsync(
        Guid ownerUserId, string? rawName, CancellationToken ct = default)
    {
        var trimmed = rawName?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 60 || !ValidCharsRegex.IsMatch(trimmed))
            return (CreateSpecimenResult.InvalidInput, null,
                "Название должно быть от 2 до 60 символов: буквы, пробел, дефис или скобки.");

        var normalized = LabAnalyteNormalizer.Normalize(trimmed);
        if (normalized.Length == 0)
            return (CreateSpecimenResult.InvalidInput, null, "Не удалось разобрать название.");

        if (SystemSpecimenNames.Contains(normalized))
            return (CreateSpecimenResult.InvalidInput, null, "Такой биоматериал уже есть в списке — выберите его выше.");

        var existing = await db.UserSpecimens
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId && s.NormalizedName == normalized, ct);
        if (existing is not null)
            return (CreateSpecimenResult.AlreadyExists, existing, null);

        // Кто-то уже провалидировал ровно это название — переиспользуем его написание бесплатно,
        // без нового LLM-вызова (см. class doc GlobalSpecimenKbService).
        string displayName;
        var globalHit = await globalKb.FindAsync(normalized, ct);
        if (globalHit is not null)
        {
            displayName = globalHit.DisplayName;
        }
        else
        {
            var (result, validatedName, reason) = await globalKb.ValidateAndRegisterAsync(trimmed, normalized, ct);
            if (result != SpecimenValidationResult.Valid)
            {
                logger.LogInformation(
                    "Биоматериал «{Name}» ({UserId}): {Result} — {Reason}", trimmed, ownerUserId, result, reason);
                return (result == SpecimenValidationResult.Unavailable
                    ? CreateSpecimenResult.Unavailable
                    : CreateSpecimenResult.Rejected, null, reason);
            }
            displayName = validatedName!;
        }

        var specimen = new UserSpecimen
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            NormalizedName = normalized,
            DisplayName = displayName,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            db.UserSpecimens.Add(specimen);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка двух одновременных запросов с одним и тем же названием — уникальный индекс
            // (OwnerUserId, NormalizedName) поймал то, что проверка выше не успела увидеть.
            var raced = await db.UserSpecimens
                .FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId && s.NormalizedName == normalized, ct);
            return raced is null ? (CreateSpecimenResult.Unavailable, null, null) : (CreateSpecimenResult.AlreadyExists, raced, null);
        }

        logger.LogInformation("Биоматериал «{Name}» добавлен пользователем {UserId}.", displayName, ownerUserId);
        return (CreateSpecimenResult.Success, specimen, null);
    }
}
