using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

public enum SpecimenValidationResult { Valid, Rejected, Unavailable }

/// <summary>Ручное переименование источника из админки (§3 плана) — без нового LLM-вызова
/// (в отличие от ValidateAndRegisterAsync, где нужно провалидировать НОВОЕ, ещё не проверенное
/// название) — админ переименовывает уже существующую, реальную запись, второй раз спрашивать
/// модель, существует ли этот источник, незачем.</summary>
public enum SpecimenRenameResult { Ok, NotFound, Conflict }

/// <summary>InUse — на строку ссылается хоть один LabIndicator/GlobalLabAnalyteKb/строка кэша
/// поиска/UserSpecimen (§3 плана: удаление справочника не должно оставлять висячие ссылки).
/// Sentinel — попытка удалить SpecimenContextIds.Unresolved, системную запись "источник не определён".</summary>
public enum SpecimenDeleteResult { Ok, NotFound, InUse, Sentinel }

/// <summary>Один источник в результате поиска по общему справочнику (GET /api/specimens/search) —
/// фронт строит по этому список автоподсказки вместо прежнего захардкоженного select'а по 6
/// значениям SpecimenType (пересборка enrich-пайплайна).</summary>
public record GlobalSpecimenDto(Guid Id, string DisplayName);

file sealed class GlobalSpecimenSearchRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Общий (не персональный) справочник источников показателя — биоматериал (кровь, моча) ИЛИ
/// небиологическое исследование (ЭКГ, УЗИ), одна таблица на оба рода понятия (пересборка
/// enrich-пайплайна: раньше был фиксированный C#-enum SpecimenType, теперь источник — полностью
/// данные, см. class doc LabIndicator.SpecimenKbId). Выносит LLM-гейт в переиспользуемый вид: то
/// же название, once провалидированное кем-то одним, находится последующими запросами БЕЗ нового
/// вызова LLM — и при ручном вводе (UserSpecimenService, свой LLM-вызов на валидацию строки), и
/// при извлечении документа (SpecimenResolver, который уже провалидировал источник в рамках
/// одного вызова на документ и переиспользует найденный/зарегистрированный здесь Id напрямую).
/// </summary>
public class GlobalSpecimenKbService(
    AppDbContext db, ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<GlobalSpecimenKbService> logger)
{
    /// <summary>Тот же порог, что раньше был в UserSpecimenService — предложенное моделью написание
    /// не должно быть другим понятием, а лишь поправленной орфографией введённого.</summary>
    private const double MinValiditySimilarity = 0.3;

    private const string SystemPrompt = """
        Ты — валидатор названий источника показателя для лабораторных анализов. На входе — короткая
        строка, которую нужно проверить как название ИСТОЧНИКА: это может быть биоматериал (кровь,
        моча, кал, мазок, слюна, ликвор и т.п.) ИЛИ вид инструментального исследования (ЭКГ, УЗИ,
        спирометрия, рентген и т.п.) — оба рода источника равноценны. Реши, является ли это
        настоящим источником, из которого может быть получен показатель для медицинского анализа, и
        верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа: {"valid": true, "displayName": "Ликвор (СМЖ)", "reason": null}

        Правила:
        - "valid": true — только если это реально существующий источник, из которого получают
          лабораторные показатели (например: ликвор, мокрота, синовиальная жидкость, сперма, пот,
          волосы, ногти, желчь, плевральная жидкость, ЭКГ, УЗИ брюшной полости, спирометрия).
        - "valid": false — если это мусор, случайный набор символов, оскорбление, название
          ПОКАЗАТЕЛЯ анализа (а не источника — например "гемоглобин", "холестерин", "СОЭ"), имя
          человека, или любое другое понятие, которое не является источником показателя. В этом
          случае "displayName": null, а "reason" — короткое пояснение по-русски, почему отклонено.
        - "displayName" при valid=true — исправленное литературное написание введённого (с
          заглавной буквы, с общепринятым сокращением в скобках, если есть) — НЕ придумывай другой
          источник, только приведи в порядок написание того, что было введено.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    /// <summary>Прямой поиск без LLM — уже провалидированное кем-то название находится бесплатно.</summary>
    public async Task<GlobalSpecimenKb?> FindAsync(string normalizedName, CancellationToken ct = default) =>
        await db.GlobalSpecimensKb.AsNoTracking().FirstOrDefaultAsync(s => s.NormalizedName == normalizedName, ct);

    /// <summary>Поиск по общему справочнику для автоподсказки при ручном выборе/правке источника
    /// показателя (GET /api/specimens/search) — тот же pg_trgm-приём, что KbAnalyteCatalogService,
    /// без LLM: справочник уже наполнен (сеяными + ранее провалидированными LLM источниками),
    /// здесь только нечёткий текстовый поиск по нему. Пустой q — первые (по алфавиту) записи, для
    /// открытия списка без ввода.</summary>
    public async Task<List<GlobalSpecimenDto>> SearchAsync(string? q, int take = 20, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 50);

        if (string.IsNullOrWhiteSpace(q))
        {
            return await db.GlobalSpecimensKb.AsNoTracking()
                .OrderBy(s => s.DisplayName)
                .Take(take)
                .Select(s => new GlobalSpecimenDto(s.Id, s.DisplayName))
                .ToListAsync(ct);
        }

        var normalized = LabAnalyteNormalizer.Normalize(q);
        var rows = await db.Database.SqlQuery<GlobalSpecimenSearchRow>($"""
            SELECT "Id", "DisplayName"
            FROM kb.global_specimens_kb
            WHERE "DisplayName" ILIKE {"%" + q + "%"} OR similarity("NormalizedName", {normalized}) > 0.2
            ORDER BY similarity("NormalizedName", {normalized}) DESC, "DisplayName"
            LIMIT {take}
            """).ToListAsync(ct);
        return rows.Select(r => new GlobalSpecimenDto(r.Id, r.DisplayName)).ToList();
    }

    /// <summary>LLM-гейт (приём "модель предлагает, детерминированный код ветирует") + запись в
    /// общий справочник при успехе — путь РУЧНОГО ввода (UserSpecimenService): нет документа, из
    /// которого можно было бы взять готовый confidence, поэтому модель валидирует строку отдельным
    /// вызовом. Вызывать только когда <see cref="FindAsync"/> не нашёл готового результата.</summary>
    public async Task<(SpecimenValidationResult Result, Guid? SpecimenKbId, string? DisplayName, string? Reason)> ValidateAndRegisterAsync(
        string trimmedRawName, string normalizedName, CancellationToken ct = default)
    {
        var prompt = await promptProvider.GetAsync("analysis.specimen-validate", SystemPrompt, ct);
        var result = await client.ExtractJsonAsync(prompt, trimmedRawName, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Валидация источника «{Name}» недоступна: {Error}", trimmedRawName, result.Error);
            return (SpecimenValidationResult.Unavailable, null, null, "Проверка временно недоступна, попробуйте позже.");
        }

        var valid = ReadBool(result.Payload, "valid");
        var reason = ReadString(result.Payload, "reason");
        if (valid != true)
        {
            logger.LogInformation("Источник «{Name}» отклонён моделью: {Reason}", trimmedRawName, reason);
            return (SpecimenValidationResult.Rejected, null, null, reason ?? "Похоже, это не источник показателя.");
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
                "Валидация источника: модель предложила «{Corrected}» вместо «{Original}», но схожесть " +
                "{Similarity:F2} слишком низкая — отклонено.", displayName, trimmedRawName, similarity);
            return (SpecimenValidationResult.Rejected, null, null, "Похоже, это не источник показателя.");
        }

        var id = await FindOrRegisterAsync(displayName, normalizedName, ct);
        return (SpecimenValidationResult.Valid, id, displayName, null);
    }

    /// <summary>Find-or-create с гонкой на уникальном индексе, без обращения к LLM — общий путь
    /// для обоих вызывающих: <see cref="ValidateAndRegisterAsync"/> выше (ручной ввод, уже
    /// провалидировал строку своим вызовом) и SpecimenResolver (документ, уже провалидировал
    /// источник в рамках одного вызова на весь документ) — тот, кто уже спросил модель, не должен
    /// спрашивать её второй раз ради того же самого решения.</summary>
    public async Task<Guid> FindOrRegisterAsync(string displayName, string normalizedName, CancellationToken ct = default)
    {
        var existing = await FindAsync(normalizedName, ct);
        if (existing is not null) return existing.Id;

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
            // SaveChangesAsync того же DbContext.
            logger.LogDebug(ex, "Общий справочник источников: гонка на «{Name}», переигрываем как чтение", normalizedName);
            db.Entry(entry).State = EntityState.Detached;

            var raced = await FindAsync(normalizedName, ct);
            return raced?.Id ?? entry.Id;
        }

        return entry.Id;
    }

    /// <summary>Переименование существующей строки из админки (§3 плана) — конфликтует, если
    /// новое написание нормализуется в уже занятое другой строкой имя (уникальный индекс по
    /// NormalizedName); в этом случае предлагать пользователю перепривязку на существующую строку
    /// (Id конфликтующей записи), а не разрешать дубль.</summary>
    public async Task<SpecimenRenameResult> RenameAsync(Guid id, string newDisplayName, CancellationToken ct = default)
    {
        var entity = await db.GlobalSpecimensKb.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return SpecimenRenameResult.NotFound;

        var trimmed = newDisplayName.Trim();
        var normalized = LabAnalyteNormalizer.Normalize(trimmed);
        if (normalized.Length == 0) return SpecimenRenameResult.Conflict;

        var conflict = await db.GlobalSpecimensKb.AnyAsync(s => s.Id != id && s.NormalizedName == normalized, ct);
        if (conflict) return SpecimenRenameResult.Conflict;

        entity.DisplayName = trimmed;
        entity.NormalizedName = normalized;
        await db.SaveChangesAsync(ct);
        return SpecimenRenameResult.Ok;
    }

    /// <summary>Удаление из админки (§3 плана) — блокируется, если строка ещё используется где-либо
    /// (реальные показатели/статьи справочника/кэш поиска/личный список), чтобы не оставлять
    /// висячие SpecimenKbId; сентинел "не определено" не удаляется никогда (нужен как фолбэк).</summary>
    public async Task<SpecimenDeleteResult> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (id == SpecimenContextIds.Unresolved) return SpecimenDeleteResult.Sentinel;

        var exists = await db.GlobalSpecimensKb.AnyAsync(s => s.Id == id, ct);
        if (!exists) return SpecimenDeleteResult.NotFound;

        var inUse =
            await db.LabIndicators.AnyAsync(i => i.SpecimenKbId == id, ct) ||
            await db.GlobalLabAnalytesKb.AnyAsync(k => k.SpecimenKbId == id, ct) ||
            await db.LabAnalyteEnrichmentJobs.AnyAsync(j => j.SpecimenKbId == id, ct) ||
            await db.LabAnalyteSearchCaches.AnyAsync(c => c.SpecimenKbId == id, ct) ||
            await db.UserSpecimens.AnyAsync(u => u.SpecimenKbId == id, ct);
        if (inUse) return SpecimenDeleteResult.InUse;

        await db.GlobalSpecimensKb.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        return SpecimenDeleteResult.Ok;
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
