using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

public enum SpecimenValidationResult { Valid, Rejected, Unavailable }

/// <summary>
/// Общий (не персональный) справочник биоматериалов вне фиксированного SpecimenType (пересборка
/// enrich-пайплайна анализов) — выносит LLM-гейт, раньше живший только в UserSpecimenService
/// (каждый пользователь провалидировал бы "ликвор" себе заново), в переиспользуемый вид: то же
/// название, once провалидированное кем-то одним, находится последующими запросами БЕЗ нового
/// вызова LLM — и при ручном вводе (UserSpecimenService), и при извлечении документа
/// (MedicalDocumentExtractionProcessor), вернувшего биоматериал вне enum.
/// </summary>
public class GlobalSpecimenKbService(AppDbContext db, ILmStudioJsonClient client, ILogger<GlobalSpecimenKbService> logger)
{
    /// <summary>Тот же порог, что раньше был в UserSpecimenService — предложенное моделью написание
    /// не должно быть другим понятием, а лишь поправленной орфографией введённого.</summary>
    private const double MinValiditySimilarity = 0.3;

    private const string SystemPrompt = """
        Ты — валидатор названий биоматериала для лабораторных анализов. На входе — короткая строка,
        которую нужно проверить как название биоматериала (кровь, моча, кал, мазок, слюна, ликвор и
        т.п.). Реши, является ли это настоящим видом биоматериала для медицинского анализа, и верни
        ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

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
          биоматериал, только приведи в порядок написание того, что было введено.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    /// <summary>Прямой поиск без LLM — уже провалидированное кем-то название находится бесплатно.</summary>
    public async Task<GlobalSpecimenKb?> FindAsync(string normalizedName, CancellationToken ct = default) =>
        await db.GlobalSpecimensKb.AsNoTracking().FirstOrDefaultAsync(s => s.NormalizedName == normalizedName, ct);

    /// <summary>LLM-гейт (приём "модель предлагает, детерминированный код ветирует" — см. class doc
    /// UserSpecimenService) + запись в общий справочник при успехе. Вызывать только когда
    /// <see cref="FindAsync"/> не нашёл готового результата.</summary>
    public async Task<(SpecimenValidationResult Result, string? DisplayName, string? Reason)> ValidateAndRegisterAsync(
        string trimmedRawName, string normalizedName, CancellationToken ct = default)
    {
        var result = await client.ExtractJsonAsync(SystemPrompt, trimmedRawName, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Валидация биоматериала «{Name}» недоступна: {Error}", trimmedRawName, result.Error);
            return (SpecimenValidationResult.Unavailable, null, "Проверка временно недоступна, попробуйте позже.");
        }

        var valid = ReadBool(result.Payload, "valid");
        var reason = ReadString(result.Payload, "reason");
        if (valid != true)
        {
            logger.LogInformation("Биоматериал «{Name}» отклонён моделью: {Reason}", trimmedRawName, reason);
            return (SpecimenValidationResult.Rejected, null, reason ?? "Похоже, это не биоматериал.");
        }

        var displayName = ReadString(result.Payload, "displayName")?.Trim();
        if (string.IsNullOrEmpty(displayName)) displayName = trimmedRawName;

        // Детерминированное вето поверх ответа модели — предложенное название не должно быть
        // другим понятием, а лишь поправленной орфографией введённого.
        var displayNormalized = LabAnalyteNormalizer.Normalize(displayName);
        var similarity = TrigramSimilarity.Similarity(displayNormalized, normalizedName);
        if (displayNormalized.Length > 0 && similarity < MinValiditySimilarity)
        {
            logger.LogWarning(
                "Валидация биоматериала: модель предложила «{Corrected}» вместо «{Original}», но схожесть " +
                "{Similarity:F2} слишком низкая — отклонено.", displayName, trimmedRawName, similarity);
            return (SpecimenValidationResult.Rejected, null, "Похоже, это не биоматериал.");
        }

        await UpsertAsync(normalizedName, displayName, ct);
        return (SpecimenValidationResult.Valid, displayName, null);
    }

    private async Task UpsertAsync(string normalizedName, string displayName, CancellationToken ct)
    {
        var exists = await db.GlobalSpecimensKb.AnyAsync(s => s.NormalizedName == normalizedName, ct);
        if (exists) return; // уже есть — не переписываем чужое подтверждённое написание

        var entry = new GlobalSpecimenKb
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            DisplayName = displayName,
            Source = "llm",
            CreatedAt = DateTime.UtcNow,
        };
        db.GlobalSpecimensKb.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Гонка на уникальном индексе — кто-то успел добавить то же самое название параллельно,
            // это не ошибка (тот же приём, что MedicationSearchCacheService.RecordSearchAsync).
            // Detach — иначе провалившаяся вставка осталась бы в трекере и ломала бы следующий
            // SaveChangesAsync того же DbContext (вызывающий код делает ещё запись после этого).
            logger.LogDebug(ex, "Общий справочник биоматериалов: гонка на «{Name}», игнорируем", normalizedName);
            db.Entry(entry).State = EntityState.Detached;
        }
    }

    private static bool? ReadBool(Dictionary<string, JsonElement> payload, string key)
    {
        if (!TryGetValue(payload, key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            _ => null,
        };
    }
}
