namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Категория величины единицы измерения — конверсия между единицами возможна только
/// внутри одной категории (масштаб: мг/дл↔г/л, нмоль/л↔пмоль/л) детерминированно по коэффициенту,
/// либо между MassConcentration и MolarConcentration — только через молярную массу КОНКРЕТНОГО
/// аналита (см. LabUnitConverter). Единицы вне этих двух категорий (%, Ед/л, сек, 10^9/л и т.п.)
/// сюда намеренно не включены: для них нет надёжной кросс-конверсии, а блокировать сравнение при
/// малейшем несовпадении написания было бы новым регрессом там, где раньше всё работало —
/// вызывающий код (IndicatorFlagCalculator) просто не трогает пару единиц, если Canonicalize()
/// хотя бы одной из них вернул null (не распознана).</summary>
public enum LabUnitDimension { MassConcentration, MolarConcentration }

/// <summary>Единица измерения, приведённая к канонической форме, с коэффициентом пересчёта в
/// базовую единицу своей категории (г/л для MassConcentration, моль/л для MolarConcentration).</summary>
public readonly record struct LabUnit(string Canonical, LabUnitDimension Dimension, double FactorToBase);

/// <summary>
/// Распознаёт единицу измерения лабораторного показателя, как её печатают разные лаборатории
/// (регистр, "мк"/"мс"/латиница вперемешку, пробелы вокруг "/"), и приводит к канонической форме с
/// коэффициентом в базовую единицу своей размерности — план "качество ИИ-распознавания анализов",
/// Этап 3: без этого справочный диапазон в ммоль/л механически сравнивался с результатом в мг/дл
/// как будто числа сопоставимы (IndicatorFlagCalculator раньше вообще не смотрел на
/// KbReferenceRange.Unit), давая ложные флаги отклонения.
/// </summary>
public static class LabUnitNormalizer
{
    private static readonly Dictionary<string, LabUnit> BySynonym = BuildTable();

    /// <summary>null — единица не распознана вовсе (опечатка, редкая форма) или не задана.</summary>
    public static LabUnit? Canonicalize(string? rawUnit)
    {
        if (string.IsNullOrWhiteSpace(rawUnit)) return null;
        return BySynonym.TryGetValue(NormalizeKey(rawUnit), out var unit) ? unit : null;
    }

    /// <summary>Нижний регистр, "мк"/"mc"/греческая "μ" → микро-знак "µ" (лаборатории пишут все
    /// три вперемешку для одной и той же приставки), пробелы убраны целиком (" мг / л" = "мг/л").</summary>
    private static string NormalizeKey(string raw)
    {
        var lower = raw.Trim().ToLowerInvariant().Replace(" ", "");
        lower = lower.Replace('μ', 'µ').Replace("мкмоль", "µmol").Replace("мк", "µ").Replace("mc", "µ");
        return lower;
    }

    private static Dictionary<string, LabUnit> BuildTable()
    {
        var table = new Dictionary<string, LabUnit>();

        void Add(LabUnitDimension dimension, double factorToBase, params string[] synonyms)
        {
            var canonical = synonyms[0];
            foreach (var synonym in synonyms) table[NormalizeKey(synonym)] = new LabUnit(canonical, dimension, factorToBase);
        }

        // Массовая концентрация, база — г/л.
        Add(LabUnitDimension.MassConcentration, 1.0, "г/л", "g/l");
        Add(LabUnitDimension.MassConcentration, 10.0, "г/дл", "g/dl");
        Add(LabUnitDimension.MassConcentration, 0.01, "мг/дл", "mg/dl");
        Add(LabUnitDimension.MassConcentration, 0.001, "мг/л", "mg/l");
        Add(LabUnitDimension.MassConcentration, 1.0, "мг/мл", "mg/ml");
        Add(LabUnitDimension.MassConcentration, 0.001, "µг/мл", "µg/ml", "мкг/мл");
        Add(LabUnitDimension.MassConcentration, 0.000_001, "µг/л", "µg/l", "мкг/л");
        Add(LabUnitDimension.MassConcentration, 0.000_001, "нг/мл", "ng/ml");
        Add(LabUnitDimension.MassConcentration, 0.000_000_001, "нг/л", "ng/l");
        Add(LabUnitDimension.MassConcentration, 0.000_000_001, "пг/мл", "pg/ml");
        Add(LabUnitDimension.MassConcentration, 0.000_000_000_001, "пг/л", "pg/l");

        // Молярная концентрация, база — моль/л.
        Add(LabUnitDimension.MolarConcentration, 1.0, "моль/л", "mol/l");
        Add(LabUnitDimension.MolarConcentration, 0.001, "ммоль/л", "mmol/l");
        Add(LabUnitDimension.MolarConcentration, 0.000_001, "µмоль/л", "µmol/l", "мкмоль/л", "umol/l");
        Add(LabUnitDimension.MolarConcentration, 0.000_000_001, "нмоль/л", "nmol/l");
        Add(LabUnitDimension.MolarConcentration, 0.000_000_000_001, "пмоль/л", "pmol/l");

        return table;
    }
}
