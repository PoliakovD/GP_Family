using FamilyHub.Infrastructure.Search;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Детерминированный запасной вариант (не LLM), когда объект исследования НЕ удалось уточнить
/// (AnalyteSubjectResolver вернул Empty — модель не уверена, шаг выключен из админки, LM Studio
/// недоступен), а ключ дедупликации показателя всё равно столкнулся между показателями из
/// РАЗНЫХ файлов одного прогона распознавания. Без разведения второй файл молча перезаписал бы
/// первый (upsert по (AnalyteKey, SpecimenKbId), см. MedicalDocumentExtractionProcessor) — это
/// хуже, чем видимый суффикс "файл N": данные хотя бы не теряются, а разбор путаницы руками
/// возможен (пользователь видит оба файла в разделе "Файлы" записи).
///
/// Возвращает суффикс ТОЛЬКО для storage-ключа/отображения — вызывающий код обязан искать в
/// справочнике (kb.global_lab_analytes_kb) и ставить обогащение по БАЗОВОМУ ключу (без суффикса),
/// иначе туда попадёт мусор вида "бактериальные микроорганизмы файл 2". Суффикс дописывается в
/// АНАЛИЗ КЛЮЧА, не в скобках — <see cref="LabAnalyteNormalizer.Normalize"/> вырезает всё в
/// скобках целиком (там живут коды/аббревиатуры вида "(HGB)"), поэтому суффикс в скобках пропал
/// бы из ключа и коллизия осталась бы.
///
/// Общая функция для MedicalDocumentExtractionProcessor (первичное сохранение) и
/// LabAnalyteKbRebuildJob (пересборка справочника) — обе точки, где показатели одной записи
/// сравниваются по ключу и могут столкнуться, должны разводить их ОДИНАКОВО, иначе пересборка
/// расходится с первичным сохранением.
/// </summary>
public static class AnalyteKeyDisambiguator
{
    /// <summary>Один показатель на входе разведения. <see cref="BaseAnalyteKey"/> — уже
    /// нормализованный ключ (LabAnalyteNormalizer.Normalize), ДО применения суффикса — коллизия
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

    /// <summary>Разводит показатели, у которых совпал <see cref="Candidate.BaseAnalyteKey"/> МЕЖДУ
    /// РАЗНЫМИ <see cref="Candidate.FileGroupId"/>. Первый файл, встретивший ключ, остаётся без
    /// суффикса (в словаре результата для него записи не будет вовсе — обратная совместимость:
    /// запись с одним файлом такого рода не меняет вид). Второй и последующие файлы с тем же
    /// ключом получают суффикс " — файл N" по порядку появления.</summary>
    public static IReadOnlyDictionary<(string BaseAnalyteKey, Guid FileGroupId), Result> Disambiguate(
        IReadOnlyList<Candidate> candidates)
    {
        var result = new Dictionary<(string, Guid), Result>();
        var fileGroupsByKey = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (!fileGroupsByKey.TryGetValue(candidate.BaseAnalyteKey, out var fileGroups))
            {
                fileGroups = [];
                fileGroupsByKey[candidate.BaseAnalyteKey] = fileGroups;
            }

            if (!fileGroups.Contains(candidate.FileGroupId)) fileGroups.Add(candidate.FileGroupId);

            var ordinal = fileGroups.IndexOf(candidate.FileGroupId); // 0 = первый файл с этим ключом
            if (ordinal == 0) continue; // первый файл — без суффикса, ключ не меняется

            var suffix = $" — файл {ordinal + 1}";
            var suffixedKey = LabAnalyteNormalizer.Normalize(candidate.BaseAnalyteKey + suffix);
            result[(candidate.BaseAnalyteKey, candidate.FileGroupId)] = new Result(suffixedKey, suffix);
        }

        return result;
    }
}
