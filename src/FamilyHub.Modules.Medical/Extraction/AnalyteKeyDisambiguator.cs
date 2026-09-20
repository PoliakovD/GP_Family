using FamilyHub.Infrastructure.Search;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Детерминированный запасной вариант (не LLM), когда объект исследования НЕ удалось уточнить
/// (AnalyteSubjectResolver вернул Empty — модель не уверена, шаг выключен из админки, LM Studio
/// недоступен), а ключ дедупликации показателя всё равно столкнулся. Коллизия бывает двух родов,
/// и обе обязаны разводиться ОДНИМ и тем же способом:
/// 1. МЕЖДУ файлами ОДНОГО прогона распознавания (несколько вложений записи, ещё не распознанных,
///    обрабатываются последовательно за один клик «Распознать») — разные <see cref="Candidate.FileGroupId"/>.
/// 2. МЕЖДУ новым файлом и УЖЕ СОХРАНЁННЫМ показателем прошлого прогона — «Распознать» обычно
///    нажимается по одному новому файлу за раз (см. class doc MedicalDocumentExtractionProcessor,
///    "повторный клик после добавления нового файла"), это не редкий, а ТИПИЧНЫЙ случай — переданный
///    вызывающим кодом <paramref name="existingAnalyteKeysForRecord"/> резервирует такие ключи,
///    чтобы новый файл никогда не получил уже занятый ими слот.
///
/// Без разведения второй файл молча перезаписал бы первый (upsert по (AnalyteKey, SpecimenKbId),
/// см. MedicalDocumentExtractionProcessor) — это хуже, чем видимый суффикс "файл N": данные хотя бы
/// не теряются, а разбор путаницы руками возможен (пользователь видит оба файла в разделе «Файлы»
/// записи).
///
/// Возвращает суффикс ТОЛЬКО для storage-ключа/отображения — вызывающий код обязан искать в
/// справочнике (kb.global_lab_analytes_kb) и ставить обогащение по БАЗОВОМУ ключу (без суффикса),
/// иначе туда попадёт мусор вида "бактериальные микроорганизмы файл 2". Суффикс дописывается в
/// САМ КЛЮЧ, не в скобках — <see cref="LabAnalyteNormalizer.NormalizeAnalyteKey"/> вырезает всё в скобках
/// целиком (там живут коды/аббревиатуры вида "(HGB)"), поэтому суффикс в скобках пропал бы из ключа
/// и коллизия осталась бы.
///
/// Общая функция для MedicalDocumentExtractionProcessor (первичное сохранение) и
/// LabAnalyteKbRebuildJob (пересборка справочника) — обе точки, где показатели одной записи
/// сравниваются по ключу и могут столкнуться, должны разводить их ОДИНАКОВО, иначе пересборка
/// расходится с первичным сохранением.
/// </summary>
public static class AnalyteKeyDisambiguator
{
    /// <summary>Один показатель на входе разведения. <see cref="BaseAnalyteKey"/> — уже
    /// нормализованный ключ (LabAnalyteNormalizer.NormalizeAnalyteKey), ДО применения суффикса — коллизия
    /// определяется по нему, не по сырому имени с бланка (которое могло отличаться регистром/
    /// пунктуацией при том же смысле). <see cref="FileGroupId"/> — кандидаты с одинаковым
    /// значением считаются повтором одной и той же строки бланка (это уже разрешил
    /// DeduplicateByName в экстракторе) и разводке не подлежат.</summary>
    public record Candidate(string BaseAnalyteKey, Guid FileGroupId);

    /// <summary><see cref="AnalyteKey"/> — новый ключ хранения/уникальности С суффиксом.
    /// <see cref="DisplaySuffix"/> — человекочитаемый хвост (" — файл 2") для DisplayName/
    /// RawDisplayName; лежит отдельно, потому что показатель может дополнительно совпасть со
    /// строкой справочника — тогда DisplayName приходит из KB, а суффикс всё равно должен быть
    /// виден пользователю, чтобы отличить одну строку от другой.</summary>
    public record Result(string AnalyteKey, string DisplaySuffix);

    /// <summary>Разводит показатели, у которых совпал <see cref="Candidate.BaseAnalyteKey"/> —
    /// МЕЖДУ РАЗНЫМИ <see cref="Candidate.FileGroupId"/> текущего прогона, а также против уже
    /// занятых ключей записи из прошлых прогонов (<paramref name="existingAnalyteKeysForRecord"/>,
    /// пусто по умолчанию — вызывающий код передаёт множество AnalyteKey уже сохранённых
    /// LabIndicator этой записи/источника). Слот "без суффикса" (сам BaseAnalyteKey как есть)
    /// достаётся первому кандидату ТЕКУЩЕГО прогона, ТОЛЬКО если он ещё не занят существующей
    /// строкой — иначе даже первый кандидат этого прогона получает суффикс, начиная с ближайшего
    /// свободного номера (пропуская номера, уже занятые существующими строками, например "файл 2"
    /// из позапрошлого прогона).</summary>
    public static IReadOnlyDictionary<(string BaseAnalyteKey, Guid FileGroupId), Result> Disambiguate(
        IReadOnlyList<Candidate> candidates, IReadOnlyCollection<string>? existingAnalyteKeysForRecord = null)
    {
        var existingKeys = existingAnalyteKeysForRecord is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(existingAnalyteKeysForRecord, StringComparer.Ordinal);

        var result = new Dictionary<(string, Guid), Result>();

        // Группируем кандидатов текущего прогона по базовому ключу, сохраняя порядок появления —
        // порядок файлов в записи (Position/UploadedAt), не порядок в словаре.
        var fileGroupsByKey = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!fileGroupsByKey.TryGetValue(candidate.BaseAnalyteKey, out var fileGroups))
                fileGroupsByKey[candidate.BaseAnalyteKey] = fileGroups = [];
            if (!fileGroups.Contains(candidate.FileGroupId)) fileGroups.Add(candidate.FileGroupId);
        }

        foreach (var (baseKey, fileGroupIds) in fileGroupsByKey)
        {
            bool SlotTaken(int ordinal) => existingKeys.Contains(KeyForOrdinal(baseKey, ordinal));

            var index = 0;
            // Первый кандидат этого прогона занимает слот "без суффикса" САМ BaseAnalyteKey),
            // только если этот слот ещё не занят существующей строкой прошлого прогона.
            if (!SlotTaken(1)) index = 1;

            var nextOrdinal = 2;
            for (; index < fileGroupIds.Count; index++)
            {
                while (SlotTaken(nextOrdinal)) nextOrdinal++;
                var suffix = $" — файл {nextOrdinal}";
                result[(baseKey, fileGroupIds[index])] = new Result(KeyForOrdinal(baseKey, nextOrdinal), suffix);
                nextOrdinal++;
            }
        }

        return result;
    }

    private static string KeyForOrdinal(string baseKey, int ordinal) =>
        ordinal == 1 ? baseKey : LabAnalyteNormalizer.NormalizeAnalyteKey(baseKey + $" — файл {ordinal}");
}
