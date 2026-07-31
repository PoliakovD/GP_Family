using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Enrichment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Единственная точка записи в kb.global_medications_kb (этап 4 — «писатель», которого явно
/// ждёт KbIsolationGuardTests.PersonalContext_CannotBeStoredInKbRow). Инвариант изоляции
/// справочника (задача 2.6) охраняется дважды: структурно (GlobalMedicationKb не имеет полей
/// под персональный контекст — см. KbIsolationGuardTests) и здесь, на уровне значений — на
/// случай, если модель случайно подмешает в текст что-то похожее на идентификатор.
/// Upsert по NormalizedName — raw SQL, как и весь остальной доступ к kb (см. KbLookupService):
/// Aliases/search_vector вне EF-модели.
/// </summary>
public class KbWriter(AppDbContext db, ILogger<KbWriter> logger)
{
    private static readonly Regex GuidPattern = new(
        @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled);

    /// <summary>7+ подряд идущих цифр — телефон/паспорт/номер карты, не имеет отношения к знанию о препарате.</summary>
    private static readonly Regex LongDigitsPattern = new(@"\d{7,}", RegexOptions.Compiled);

    /// <summary>То же множество ключевых слов, что и KbIsolationGuardTests.PersonalContextPattern —
    /// один инвариант, проверяемый на двух уровнях (структура модели + значения payload).</summary>
    private static readonly Regex PersonalKeywordPattern = new(
        @"\b(UserId|FamilyId|Person|Owner|Telegram|Email|Phone|Member)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<KbWriteResult> UpsertAsync(
        string normalizedName, string displayName, MedicationSummary summary, string source, CancellationToken ct = default)
    {
        var violation = FindPersonalContextViolation(displayName, summary);
        if (violation is not null)
        {
            logger.LogWarning(
                "Запись в справочник «{DisplayName}» отклонена: подозрение на персональный контекст ({Violation}).",
                displayName, violation);
            return KbWriteResult.Rejected($"Payload содержит подозрение на персональный контекст: {violation}");
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            schemaVersion = MedicationSummarySchema.CurrentVersion,
            internationalName = summary.InternationalName,
            tradeNames = summary.TradeNames,
            form = summary.Form,
            purpose = summary.Purpose,
            usage = summary.Usage,
            storage = summary.Storage,
            driving = summary.Driving,
            specialNotes = summary.SpecialNotes,
        });

        // Алиасы — нормализованные торговые названия (та же функция, что и ключ дедупликации),
        // без самого NormalizedName (иначе он же попал бы и в основной ключ, и в алиасы).
        var aliases = summary.TradeNames
            .Select(MedicationNameNormalizer.Normalize)
            .Where(a => a.Length > 0 && a != normalizedName)
            .Distinct()
            .ToArray();

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO kb.global_medications_kb
                ("Id", "NormalizedName", "DisplayName", "PayloadJson", "PayloadVersion", "Source", "Aliases", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {normalizedName}, {displayName}, {payloadJson}::jsonb, {MedicationSummarySchema.CurrentVersion}, {source}, {aliases}, {now}, {now})
            ON CONFLICT ("NormalizedName") DO UPDATE SET
                "DisplayName" = EXCLUDED."DisplayName",
                "PayloadJson" = EXCLUDED."PayloadJson",
                "PayloadVersion" = EXCLUDED."PayloadVersion",
                "Source" = EXCLUDED."Source",
                "Aliases" = ARRAY(SELECT DISTINCT unnest(kb.global_medications_kb."Aliases" || EXCLUDED."Aliases")),
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);

        // ExecuteSqlInterpolatedAsync не возвращает строки (ON CONFLICT мог вернуть Id уже
        // существующей записи, не сгенерированный выше) — читаем фактический Id отдельным SELECT.
        var actualId = await db.Database.SqlQuery<KbIdRow>($"""
            SELECT "Id" FROM kb.global_medications_kb WHERE "NormalizedName" = {normalizedName}
            """).Select(r => r.Id).SingleAsync(ct);

        logger.LogInformation("Справочник пополнен: «{DisplayName}» ({NormalizedName}), источник: {Source}.",
            displayName, normalizedName, source);
        return KbWriteResult.Ok(actualId);
    }

    private static string? FindPersonalContextViolation(string displayName, MedicationSummary summary)
    {
        var candidates = new List<string?>
        {
            displayName, summary.InternationalName, summary.Form, summary.Purpose, summary.Usage,
            summary.Storage, summary.Driving, summary.SpecialNotes,
        };
        candidates.AddRange(summary.TradeNames);

        foreach (var text in candidates)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (GuidPattern.IsMatch(text)) return $"похоже на GUID: \"{text}\"";
            if (EmailPattern.IsMatch(text)) return $"похоже на e-mail: \"{text}\"";
            if (LongDigitsPattern.IsMatch(text)) return $"длинная числовая последовательность: \"{text}\"";
            if (PersonalKeywordPattern.IsMatch(text)) return $"персональный ключ в тексте: \"{text}\"";
        }

        return null;
    }
}
