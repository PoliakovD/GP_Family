using System.Text.Json;
using FamilyHub.Domain.Vaccinations;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>Одна строка, найденная на фото сертификата. DoseIndex не определяется здесь — сервис не
/// знает, для какого человека распознаёт (recognize — до выбора «Кому»), и не может судить, какая
/// именно доза серии это была; сопоставление серия→доза достраивает фронт по уже загруженному графику
/// человека (первая ещё не отмеченная доза серии), пользователь может поправить перед сохранением.</summary>
public record VaccinationRecognizedItem(
    string? SeriesCode, string RawName, string? VaccineName, DateOnly? Date, VaccinationDatePrecision? DatePrecision, bool NeedsReview);

public record VaccinationRecognitionResponse(bool Success, List<VaccinationRecognizedItem> Items, string? Error);

/// <summary>
/// Распознавание сертификата прививок по фото через локальную vision-LLM — тот же приём, что
/// <c>MedicationOcrService</c>: синхронно, фото используются только в рамках запроса и не
/// сохраняются здесь (сохраняются позже, только если пользователь подтвердит находки —
/// см. <see cref="VaccinationCertificateService"/>).
/// </summary>
public class VaccinationCertificateOcrService(
    ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<VaccinationCertificateOcrService> logger)
{
    private const int MaxPhotos = 8;
    private const long MaxPhotoSizeBytes = 2 * 1024 * 1024;

    private const string UserText =
        "Распознай записи о прививках на этих страницах сертификата (может быть несколько фото одного документа).";

    private const string SystemPrompt = """
        Ты — оцифровщик сертификата профилактических прививок (форма 156/у-93 или аналог).
        Проанализируй все прикреплённые фотографии страниц ОДНОГО сертификата и верни ТОЛЬКО
        валидный JSON-объект, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        { "items": [
            { "name": "Название прививки или инфекции, как написано в сертификате",
              "vaccine": "Название вакцины/производитель, если указано, иначе null",
              "date": "Дата в формате dd/MM/yyyy, либо MM/yyyy, либо yyyy — как удалось разобрать",
              "confident": true }
        ] }

        Правила:
        - Одна запись сертификата (одна дата) — один элемент массива; для многодозных вакцин
          (БЦЖ, АКДС, полиомиелит и т.п.) каждая отдельная отметка — свой элемент, не объединяй дозы.
        - "confident": false, если почерк или качество фото не позволяют уверенно прочитать дату или название.
        - Если дата совсем не разборчива — верни null в "date", а не выдуманную дату.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<VaccinationRecognitionResponse> RecognizeAsync(IFormFileCollection files, CancellationToken ct = default)
    {
        if (files.Count == 0) return Failure("Прикрепите хотя бы одно фото.");
        if (files.Count > MaxPhotos) return Failure($"Можно прикрепить не более {MaxPhotos} фото.");

        var images = new List<(byte[] Bytes, string ContentType)>();
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.ContentType) || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return Failure("Допустимы только изображения.");
            if (file.Length > MaxPhotoSizeBytes)
                return Failure($"Каждое фото должно быть не больше {MaxPhotoSizeBytes / (1024 * 1024)} МБ.");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            images.Add((ms.ToArray(), file.ContentType));
        }

        var prompt = await promptProvider.GetAsync("vaccination.certificate-ocr", SystemPrompt, ct);
        var result = await client.ExtractJsonAsync(prompt, UserText, images, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Распознавание сертификата прививок не удалось: {Error}", result.Error);
            return Failure(result.Error ?? "Не удалось распознать сертификат.");
        }

        if (!TryGetValue(result.Payload, "items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            return Failure("Модель не нашла ни одной записи на фото.");

        var items = new List<VaccinationRecognizedItem>();
        foreach (var itemEl in itemsEl.EnumerateArray())
        {
            if (itemEl.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetProperty(itemEl, "name", out var nameEl)) continue;
            var name = Stringify(nameEl).Trim();
            if (name.Length == 0) continue;

            var vaccine = TryGetProperty(itemEl, "vaccine", out var vaccineEl) ? Stringify(vaccineEl).Trim() : null;
            var confident = !TryGetProperty(itemEl, "confident", out var confidentEl) || confidentEl.ValueKind != JsonValueKind.False;
            var (date, precision) = ParseDate(TryGetProperty(itemEl, "date", out var dateEl) ? Stringify(dateEl).Trim() : null);

            var seriesCode = MatchCatalog(name) ?? MatchCatalog(vaccine ?? "");
            items.Add(new VaccinationRecognizedItem(
                seriesCode, name, string.IsNullOrWhiteSpace(vaccine) ? null : vaccine, date, precision,
                NeedsReview: !confident || seriesCode is null || date is null));
        }

        return new VaccinationRecognitionResponse(true, items, null);
    }

    /// <summary>Сопоставление найденного текста с серией каталога — простое нормализованное
    /// вхождение по названию/торговым названиям/инфекциям, без триграмм и веб-поиска (масштаб задачи
    /// не тот же, что у справочника препаратов/показателей): фото сертификата сравнивается с
    /// полутора десятками известных серий, не с открытым множеством названий. Промах — не ошибка,
    /// пользователь довыберет серию вручную (см. NeedsReview).</summary>
    private static string? MatchCatalog(string rawName)
    {
        var norm = Normalize(rawName);
        if (norm.Length == 0) return null;

        foreach (var series in VaccineCatalog.All)
        {
            var candidates = new[] { series.Name, series.ShortName }
                .Concat(series.Diseases).Concat(series.TradeNames);
            if (candidates.Any(c =>
                {
                    var cn = Normalize(c);
                    return cn.Length > 0 && (cn == norm || norm.Contains(cn) || cn.Contains(norm));
                }))
                return series.Code;
        }
        return null;
    }

    private static string Normalize(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static (DateOnly? Date, VaccinationDatePrecision? Precision) ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        raw = raw.Trim();

        if (DateOnly.TryParseExact(raw, "dd/MM/yyyy", out var day)) return (day, VaccinationDatePrecision.Day);
        if (DateOnly.TryParseExact(raw, "MM/yyyy", out var month)) return (month, VaccinationDatePrecision.Month);
        if (raw.Length == 4 && int.TryParse(raw, out var year) && year is > 1900 and < 2200)
            return (new DateOnly(year, 1, 1), VaccinationDatePrecision.Year);
        return (null, null);
    }

    private static VaccinationRecognitionResponse Failure(string error) => new(false, [], error);

    private static bool TryGetValue(Dictionary<string, JsonElement> payload, string key, out JsonElement value)
    {
        foreach (var (k, v) in payload)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        }
        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase)) { value = prop.Value; return true; }
        }
        value = default;
        return false;
    }

    private static string Stringify(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };
}
