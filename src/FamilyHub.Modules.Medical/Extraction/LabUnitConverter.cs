using FamilyHub.Infrastructure.Search;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Переводит числовое значение из одной единицы измерения концентрации в другую (план "качество
/// ИИ-распознавания анализов", Этап 3) — живой сценарий: справочник (GlobalLabAnalyteKb) даёт норму
/// глюкозы в ммоль/л, а конкретный бланк напечатал результат в мг/дл — без конверсии
/// IndicatorFlagCalculator сравнивал бы "4.41" (ммоль/л) с границами "70-100" (мг/дл) напрямую,
/// давая заведомо неверный флаг.
///
/// Внутри одной размерности (масса↔масса, моль↔моль — LabUnitNormalizer.LabUnitDimension) конверсия
/// чисто арифметическая (масштаб приставки). МЕЖДУ массой и количеством вещества конверсия
/// физически возможна ТОЛЬКО зная молярную массу конкретного вещества (масса = моль × молярная
/// масса) — отсюда MolarMassByAnalyteKey ниже: небольшая курируемая таблица для аналитов, которые
/// реально печатаются лабораториями то в тех, то в других единицах. Аналит, которого в таблице нет
/// (и любая другая пара единиц, которую нельзя свести к одной размерности) — TryConvert возвращает
/// false, а не приблизительное число: вызывающий код (IndicatorFlagCalculator) в этом случае обязан
/// отказаться от сравнения (RefSource с пояснением "норма в другой единице"), а не тихо считать
/// диапазон неприменимым И не сравнивать напрямую как раньше.
/// </summary>
public static class LabUnitConverter
{
    /// <summary>Молярная масса, г/моль — по формуле самого измеряемого вещества (не соли/эфира,
    /// если бланк не уточняет форму отдельно). Один и тот же аналит на разных бланках называется
    /// по-разному (общий/прямой/непрямой билирубин, общий/ионизированный кальций и т.п.) — все
    /// варианты названия ведут к одной и той же молярной массе самого вещества, поэтому ключ —
    /// НЕ дословное имя показателя, а нормализованное имя ВЕЩЕСТВА (см. Keys ниже,
    /// LabAnalyteNormalizer.NormalizeAnalyteKey — тот же ключ, что и LabIndicator.AnalyteKey, чтобы
    /// вызывающему коду достаточно было передать уже вычисленный AnalyteKey без повторной
    /// нормализации).</summary>
    private static readonly Dictionary<string, double> MolarMassByAnalyteKey = BuildMolarMassTable();

    /// <summary>Пытается перевести value из fromUnit в toUnit. analyteKey нужен, ТОЛЬКО когда
    /// единицы разных размерностей (масса↔моль) — для чисто масштабной конверсии внутри одной
    /// размерности (мг/дл↔г/л) он не используется вовсе, можно передавать null.</summary>
    public static bool TryConvert(double value, string? fromUnit, string? toUnit, string? analyteKey, out double converted)
    {
        converted = 0;
        var from = LabUnitNormalizer.Canonicalize(fromUnit);
        var to = LabUnitNormalizer.Canonicalize(toUnit);
        if (from is null || to is null) return false;

        if (from.Value.Canonical == to.Value.Canonical)
        {
            converted = value;
            return true;
        }

        if (from.Value.Dimension == to.Value.Dimension)
        {
            // Одна размерность — чистый масштаб: значение → базовая единица размерности → целевая.
            converted = value * from.Value.FactorToBase / to.Value.FactorToBase;
            return true;
        }

        // Разные размерности (масса↔моль) — нужна молярная масса КОНКРЕТНОГО аналита.
        if (string.IsNullOrWhiteSpace(analyteKey)) return false;
        if (!MolarMassByAnalyteKey.TryGetValue(analyteKey.Trim().ToLowerInvariant(), out var molarMass)) return false;

        // Оба приводятся к базовым единицам своих размерностей (г/л и моль/л), затем г/л = моль/л
        // × молярная масса (г/моль) — и обратно, в зависимости от того, что откуда переводим.
        var baseValue = value * from.Value.FactorToBase;
        double baseTargetValue = from.Value.Dimension == LabUnitDimension.MassConcentration
            ? baseValue / molarMass  // г/л → моль/л
            : baseValue * molarMass; // моль/л → г/л
        converted = baseTargetValue / to.Value.FactorToBase;
        return true;
    }

    private static Dictionary<string, double> BuildMolarMassTable()
    {
        var table = new Dictionary<string, double>();

        void Add(double molarMassGramsPerMole, params string[] names)
        {
            foreach (var name in names)
            {
                var key = LabAnalyteNormalizer.NormalizeAnalyteKey(name);
                if (key.Length > 0) table[key] = molarMassGramsPerMole;
            }
        }

        Add(180.16, "Глюкоза");
        Add(60.06, "Мочевина");
        Add(113.12, "Креатинин");
        Add(168.11, "Мочевая кислота");
        Add(386.65, "Холестерин общий", "Холестерин", "Холестерин ЛПНП", "Холестерин ЛПВП", "ЛПНП", "ЛПВП");
        Add(885.4, "Триглицериды");
        Add(584.66, "Билирубин общий", "Билирубин прямой", "Билирубин непрямой", "Билирубин");
        Add(40.08, "Кальций общий", "Кальций", "Кальций ионизированный");
        Add(24.31, "Магний");
        Add(55.85, "Железо", "Железо сывороточное");
        Add(1355.4, "Витамин B12", "Витамин В12", "Цианокобаламин");
        Add(441.4, "Фолиевая кислота", "Фолат");
        Add(400.64, "Витамин D", "25-ОН витамин D", "25-гидроксивитамин D");
        Add(288.4, "Тестостерон", "Тестостерон общий", "Тестостерон свободный");
        Add(362.46, "Кортизол");
        Add(314.5, "Прогестерон");
        Add(272.4, "Эстрадиол");
        Add(776.87, "Т4 свободный", "Свободный Т4", "Тироксин свободный");
        Add(650.97, "Т3 свободный", "Свободный Т3", "Трийодтиронин свободный");

        return table;
    }
}
