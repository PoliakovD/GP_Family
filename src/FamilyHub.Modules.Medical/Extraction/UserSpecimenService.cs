using System.Text.RegularExpressions;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

public enum CreateSpecimenResult { Success, AlreadyExists, Rejected, Unavailable, InvalidInput }

/// <summary>
/// Справочник биоматериалов на пользователя (UX-редизайн) — когда фиксированного
/// <see cref="Domain.Enums.SpecimenType"/> не хватает ("ликвор", "мокрота" и т.п.). Пользователь
/// вводит название один раз, LLM проверяет, что это настоящий биоматериал, а не мусор/ругательство/
/// название ПОКАЗАТЕЛЯ анализа — дальше значение живёт в личном справочнике и предлагается
/// автоподсказкой (см. GET /api/specimens).
///
/// Приём "модель предлагает, детерминированный код ветирует" — зеркало
/// MedicationEnrichmentProcessor.ResolveCorrectedName: LLM может нормализовать написание
/// ("ликвор" → "Ликвор (СМЖ)"), но если предложенное слишком далеко от введённого по триграммам,
/// это значит модель подменила понятие целиком, а не поправила орфографию — отклоняем.
/// LM Studio недоступен → Unavailable, а не "принять на веру": запись ушла бы в справочник
/// пользователя навсегда, тихий пропуск валидации хуже честного отказа (тот же принцип, что и у
/// остальных LLM-гейтов модуля).
/// </summary>
public class UserSpecimenService(AppDbContext db, ILmStudioJsonClient client, ILogger<UserSpecimenService> logger)
{
    private const double MinValiditySimilarity = 0.3;

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

    private const string SystemPrompt = """
        Ты — валидатор пользовательских названий биоматериала для лабораторных анализов. На входе
        — короткая строка, которую человек ввёл как название биоматериала (кровь, моча, кал, мазок,
        слюна, ликвор и т.п.). Реши, является ли это настоящим видом биоматериала для медицинского
        анализа, и верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа: {"valid": true, "displayName": "Ликвор (СМЖ)", "reason": null}

        Правила:
        - "valid": true — только если это реально существующий биоматериал, которым сдают
          лабораторные анализы (например: ликвор, мокрота, синовиальная жидкость, сперма, пот,
          волосы, ногти, желчь, плевральная жидкость).
        - "valid": false — если это мусор, случайный набор символов, оскорбление, название
          ПОКАЗАТЕЛЯ анализа (а не биоматериала — например "гемоглобин", "холестерин", "СОЭ"), имя
          человека, или любое другое понятие, которое не является видом биоматериала. В этом
          случае "displayName": null, а "reason" — короткое пояснение по-русски, почему отклонено.
        - "displayName" при valid=true — исправленное литературное написание введённого (с
          заглавной буквы, с общепринятым сокращением в скобках, если есть) — НЕ придумывай другой
          биоматериал, только приведи в порядок написание того, что ввёл пользователь.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

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

        var result = await client.ExtractJsonAsync(SystemPrompt, trimmed, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation(
                "Валидация биоматериала «{Name}» ({UserId}) недоступна: {Error}", trimmed, ownerUserId, result.Error);
            return (CreateSpecimenResult.Unavailable, null, "Проверка временно недоступна, попробуйте позже.");
        }

        var valid = ReadBool(result.Payload, "valid");
        var reason = ReadString(result.Payload, "reason");
        if (valid != true)
        {
            logger.LogInformation(
                "Биоматериал «{Name}» отклонён моделью ({UserId}): {Reason}", trimmed, ownerUserId, reason);
            return (CreateSpecimenResult.Rejected, null, reason ?? "Похоже, это не биоматериал.");
        }

        var displayName = ReadString(result.Payload, "displayName")?.Trim();
        if (string.IsNullOrEmpty(displayName)) displayName = trimmed;

        // Детерминированное вето поверх ответа модели (см. class doc) — предложенное название не
        // должно быть другим понятием, а лишь поправленной орфографией введённого.
        var displayNormalized = LabAnalyteNormalizer.Normalize(displayName);
        var similarity = TrigramSimilarity.Similarity(displayNormalized, normalized);
        if (displayNormalized.Length > 0 && similarity < MinValiditySimilarity)
        {
            logger.LogWarning(
                "Валидация биоматериала: модель предложила «{Corrected}» вместо «{Original}», но схожесть " +
                "{Similarity:F2} слишком низкая — отклонено ({UserId}).", displayName, trimmed, similarity, ownerUserId);
            return (CreateSpecimenResult.Rejected, null, "Похоже, это не биоматериал.");
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

    private static bool? ReadBool(Dictionary<string, System.Text.Json.JsonElement> payload, string key)
    {
        if (!TryGetValue(payload, key, out var el)) return null;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            _ => null,
        };
    }
}
