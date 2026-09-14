using System.Globalization;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Сравнивает распознанный показатель с референсным диапазоном (ветка medicalrecords, редизайн
/// v2) — каскад приоритетов (см. FamilyHub.Domain.Enums.RefSource):
/// 1. Референс из самого бланка — лаборатория печатает диапазон под свою методику/единицы, числом
///    (refLow/refHigh) или текстом ("&lt;47", "&gt;47", "отрицательно" — см. ReferenceRangeTextParser/
///    QualitativeResultClassifier ниже).
/// 2. Фиксированный диапазон из GlobalLabAnalyteKb, подобранный по полу (identity rework:
///    User.Gender/FamilyDependent.Gender) и возрасту пациента.
/// Диапазон, посчитанный локальной LLM по методике из KB (RefSource.KbCalculated) — ТРЕТИЙ шаг
/// каскада, но он не умещается в этот чистый компаратор (требует внешнего вызова) — см.
/// PatientReferenceCalculator и ApplyCalculatedRange ниже, вызывается процессором отдельно, когда
/// этот метод вернул RefSource.None, а у KB-записи есть CalculationInstructions.
/// 4. Ожидаемая норма от МОДЕЛИ (RefSource.Inferred) — тоже не умещается в Calculate (должна
///    уступать шагу 3 выше), см. TryApplyInferred ниже — наименее надёжный шаг, вызывается
///    процессором/job'ом только когда шаги 1-3 не дали результата.
/// </summary>
public static class IndicatorFlagCalculator
{
    /// <summary>Значение внутри диапазона, но ближе чем на 5% к границе к КРИТИЧЕСКОМУ выходу —
    /// пока не считаем: различение Critical пока не подтверждено бланком/справочником, только
    /// Low/Normal/High. Оставлено на будущее расширение (например, явный "критический" диапазон
    /// в справочнике) — сейчас Critical не выставляется никогда.</summary>
    public static (IndicatorFlag Flag, RefSource Source, double? EffectiveLow, double? EffectiveHigh) Calculate(
        ExtractedLabIndicator indicator, KbReferenceRange? kbFallback, int? ageYears, Gender? sex)
    {
        // Censored-значение самого показателя ("<0,5", ">1000" — за пределами чувствительности
        // метода) — числовая часть служит точкой сравнения (см. ReferenceRangeTextParser).
        var numericValue = ParseNumeric(indicator.Value) ?? ReferenceRangeTextParser.ParseCensoredValue(indicator.Value);

        // 1. Числовые границы бланка — явные refLow/refHigh, либо разобранный текстовый refText
        //    ("<47" → (0, 47), ">47" → (47, null), а также обычный "130-160", если модель всё же
        //    положила его в refText целиком, а не разложила).
        var refLow = indicator.RefLow;
        var refHigh = indicator.RefHigh;
        if (refLow is null && refHigh is null && !string.IsNullOrWhiteSpace(indicator.RefText))
        {
            var parsed = ReferenceRangeTextParser.TryParse(indicator.RefText);
            if (parsed is not null) (refLow, refHigh) = parsed.Value;
        }
        if (refLow is not null || refHigh is not null)
        {
            return (CompareToRange(numericValue, refLow, refHigh), RefSource.Blank, refLow, refHigh);
        }

        // 2. Качественный референс напечатан в бланке ("отрицательно", "не обнаружено") —
        //    сравниваем по СМЫСЛУ (полярность: найдено/не найдено), не голым равенством строк —
        //    "не обнаружены" должно совпасть с нормой "не обнаружено" (см. QualitativeResultClassifier).
        //    Формулировки, которых классификатор не разобрал ни у значения, ни у референса,
        //    падают на прежнее точное сравнение строк — не отбираем то немногое, что уже работало.
        if (!string.IsNullOrWhiteSpace(indicator.RefText))
        {
            return (CompareQualitative(indicator.Value, indicator.RefText), RefSource.Blank, null, null);
        }

        // 3. Референса в бланке нет вовсе, но само значение — однозначный отрицательный
        //    результат ("не обнаружено", без колонки нормы — типичный бланк ИППП/качественной
        //    панели) — это осмысленная норма, а не "нет данных": для такого теста "не обнаружено"
        //    и есть здоровый результат. Никакого бейджа ИИ — в бланке буквально так и написано,
        //    ничего не выведено из общих знаний (см. §5 ниже, TryApplyInferred, — та другая ветка).
        if (QualitativeResultClassifier.Classify(indicator.Value) == QualitativePolarity.NegativeFinding)
        {
            return (IndicatorFlag.Normal, RefSource.Blank, null, null);
        }

        // 4. Фиксированный диапазон KB — только для НАСТОЯЩЕГО числового значения: раньше
        //    нечисловое значение против числового KB-диапазона давало (Unknown, KbFixed) и
        //    навсегда блокировало дальнейший пересчёт (KbFixed в приоритете и не уступает место
        //    ни KbCalculated, ни Inferred) — должно проваливаться дальше по каскаду, а не застревать.
        if (numericValue is not null && kbFallback is not null && MatchesPatient(kbFallback, ageYears, sex))
        {
            return (CompareToRange(numericValue, kbFallback.Low, kbFallback.High), RefSource.KbFixed, kbFallback.Low, kbFallback.High);
        }

        return (IndicatorFlag.Unknown, RefSource.None, null, null);
    }

    /// <summary>Применяет уже посчитанный диапазон (PatientReferenceCalculator, RefSource.KbCalculated) —
    /// тот же числовой компаратор, что Calculate, чтобы не дублировать пороговую логику.</summary>
    public static IndicatorFlag ApplyCalculatedRange(string value, double? low, double? high) =>
        CompareToRange(ParseNumeric(value) ?? ReferenceRangeTextParser.ParseCensoredValue(value), low, high);

    /// <summary>Последний, наименее надёжный шаг каскада (RefSource.Inferred, план "нормы из
    /// знаний модели") — ExtractedLabIndicator.RefExpected заполняет модель ТОЛЬКО когда решила,
    /// что в бланке референса нет вовсе. Вызывается явно процессором/job'ом, а не изнутри
    /// Calculate — тот должен первым успеть попробовать KbFixed и (если в KB есть методика расчёта)
    /// KbCalculated, которые надёжнее догадки модели; Calculate ничего не знает о том, шёл ли уже
    /// такой попытка расчёта, только вызывающая сторона (см. MedicalDocumentExtractionProcessor).
    /// Null, если RefExpected пуст или классификатор/парсер не смогли его разобрать — в этом случае
    /// вызывающая сторона оставляет прежний (Unknown, None).</summary>
    public static (IndicatorFlag Flag, RefSource Source, double? Low, double? High)? TryApplyInferred(
        ExtractedLabIndicator indicator)
    {
        if (string.IsNullOrWhiteSpace(indicator.RefExpected)) return null;

        var numericValue = ParseNumeric(indicator.Value) ?? ReferenceRangeTextParser.ParseCensoredValue(indicator.Value);

        var range = ReferenceRangeTextParser.TryParse(indicator.RefExpected);
        if (range is not null)
        {
            return (CompareToRange(numericValue, range.Value.Low, range.Value.High), RefSource.Inferred, range.Value.Low, range.Value.High);
        }

        var expectedPolarity = QualitativeResultClassifier.Classify(indicator.RefExpected);
        if (expectedPolarity != QualitativePolarity.Unknown)
        {
            var valuePolarity = QualitativeResultClassifier.Classify(indicator.Value);
            if (valuePolarity != QualitativePolarity.Unknown)
            {
                var flag = valuePolarity == expectedPolarity ? IndicatorFlag.Normal : IndicatorFlag.High;
                return (flag, RefSource.Inferred, null, null);
            }
        }

        return null;
    }

    /// <summary>Качественное сравнение "по смыслу" (см. QualitativeResultClassifier) с фолбэком на
    /// точное равенство строк для формулировок, которые классификатор не разобрал ни у значения,
    /// ни у референса — тот же результат, что был раньше у ЛЮБОЙ формулировки, просто теперь не
    /// единственный путь к Normal. Явное РАСХОЖДЕНИЕ полярностей ("положительно" при норме
    /// "отрицательно") теперь High, а не Unknown — прежде очевидное отклонение молчало серым "?".</summary>
    private static IndicatorFlag CompareQualitative(string value, string refText)
    {
        var valuePolarity = QualitativeResultClassifier.Classify(value);
        var refPolarity = QualitativeResultClassifier.Classify(refText);
        if (valuePolarity != QualitativePolarity.Unknown && refPolarity != QualitativePolarity.Unknown)
        {
            return valuePolarity == refPolarity ? IndicatorFlag.Normal : IndicatorFlag.High;
        }

        return string.Equals(value.Trim(), refText.Trim(), StringComparison.OrdinalIgnoreCase)
            ? IndicatorFlag.Normal
            : IndicatorFlag.Unknown;
    }

    /// <summary>Диапазон под конкретные пол+возраст, если есть; иначе общий (без ограничений),
    /// иначе первый попавшийся — лучше приблизительный ориентир, чем никакого. Общий для
    /// MedicalDocumentExtractionProcessor (свежее распознавание) и RecalculateIndicatorFlagsJob
    /// (дозаполнение задним числом после того, как справочник наполнился).</summary>
    public static KbReferenceRange? PickBestRange(List<KbReferenceRange> ranges, int? ageYears, Gender? sex)
    {
        var index = PickBestRangeIndex(ranges, ageYears, sex);
        return index is null ? null : ranges[index.Value];
    }

    /// <summary>Тот же выбор, что PickBestRange, но возвращает ИНДЕКС в исходном списке — нужен
    /// панели справки (редизайн v2, PR4-BE), которая подсвечивает строку "Нормы" в статье
    /// справочника (KbAnalyteCard.RefRanges — тот же порядок, что здесь). Индекс, а не сам
    /// объект: KbReferenceRange/KbRefRangeDto — разные типы (первый для каскада расчёта статуса,
    /// второй — DTO ответа), сравнивать их по значению было бы лишней связкой между слоями.</summary>
    public static int? PickBestRangeIndex(List<KbReferenceRange> ranges, int? ageYears, Gender? sex)
    {
        if (ranges.Count == 0) return null;

        // Индексы всегда относительно ОРИГИНАЛЬНОГО списка ranges (контракт метода — см. doc выше),
        // даже после фильтров ниже: панель справки подсвечивает строку по этому индексу в
        // KbAnalyteCard.RefRanges, который зеркалит исходный порядок как есть.
        var indexed = ranges.Select((r, i) => (Range: r, Index: i)).ToList();

        // Автоматическое сравнение годится только для обычных числовых диапазонов общей/детской
        // популяции — для Pregnancy/CyclePhase в домене нет сигнала (беременность/фаза цикла
        // нигде не хранятся), а Qualitative не число; такие строки остаются только в статье
        // справочника (см. class doc LabPopulation). Если после фильтра ничего не осталось (KB-
        // запись до пересборки, только Pregnancy/Qualitative-строки) — используем полный список,
        // как раньше, лучше приблизительный ориентир, чем никакого.
        var autoMatchable = indexed
            .Where(x => x.Range.NormKind == LabNormKind.FixedRange &&
                        x.Range.Population is LabPopulation.General or LabPopulation.Children)
            .ToList();
        if (autoMatchable.Count > 0) indexed = autoMatchable;

        var bySexIndexed = sex is null
            ? indexed
            : indexed.Where(x => x.Range.Sex is null || x.Range.Sex == sex).ToList();
        if (bySexIndexed.Count == 0) bySexIndexed = indexed;

        if (ageYears is not null)
        {
            var ageMatch = bySexIndexed.FirstOrDefault(x =>
                (x.Range.AgeFrom is not null || x.Range.AgeTo is not null) &&
                (x.Range.AgeFrom is null || ageYears >= x.Range.AgeFrom) &&
                (x.Range.AgeTo is null || ageYears <= x.Range.AgeTo));
            if (ageMatch.Range is not null) return ageMatch.Index;
        }

        var generalMatch = bySexIndexed.FirstOrDefault(x => x.Range.AgeFrom is null && x.Range.AgeTo is null);
        return generalMatch.Range is not null ? generalMatch.Index : bySexIndexed[0].Index;
    }

    private static IndicatorFlag CompareToRange(double? numericValue, double? refLow, double? refHigh)
    {
        if (numericValue is null || (refLow is null && refHigh is null)) return IndicatorFlag.Unknown;
        if (refLow is not null && numericValue < refLow) return IndicatorFlag.Low;
        if (refHigh is not null && numericValue > refHigh) return IndicatorFlag.High;
        return IndicatorFlag.Normal;
    }

    /// <summary>Диапазон с указанным полом подходит только пациенту того же пола (или неизвестного —
    /// тогда лучше промолчать, чем ошибочно сравнить с чужим полом); диапазон без пола — универсальный.</summary>
    private static bool MatchesPatient(KbReferenceRange range, int? ageYears, Gender? sex)
    {
        if (range.Sex is not null && range.Sex != sex) return false;
        if (range.AgeFrom is null && range.AgeTo is null) return true;
        if (ageYears is null) return false;
        return (range.AgeFrom is null || ageYears >= range.AgeFrom) && (range.AgeTo is null || ageYears <= range.AgeTo);
    }

    /// <summary>"118", "5.6", "5,6" (русская десятичная запятая из бланков) → double. Не число
    /// ("отрицательно", "1-3 в п/зр") → null, флаг для такого значения считается по RefText выше.</summary>
    private static double? ParseNumeric(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}

/// <summary>Один диапазон из GlobalLabAnalyteKb.PayloadJson.refRanges — используется только когда
/// бланк не напечатал собственный референс (см. Calculate). Sex=null — общий диапазон, годится
/// любому полу; Sex задан — годится только пациенту того же пола (см. MatchesPatient). NormKind/
/// Population — систематизированные категории (пересборка enrich-пайплайна, см.
/// LabAnalyteReferenceRange) — PickBestRangeIndex фильтрует по ним ДО сопоставления пола/возраста.
/// SourceDomain/SourceRank — откуда взят диапазон при merge (см. ReferenceRangeMerger), для
/// отображения источника в статье справочника, в каскад расчёта статуса не участвуют.</summary>
public record KbReferenceRange(
    int? AgeFrom, int? AgeTo, Gender? Sex, double? Low, double? High, string? Unit,
    LabNormKind NormKind = LabNormKind.FixedRange,
    LabPopulation Population = LabPopulation.General,
    string? PopulationDetail = null,
    string? SourceDomain = null,
    int SourceRank = 0);
