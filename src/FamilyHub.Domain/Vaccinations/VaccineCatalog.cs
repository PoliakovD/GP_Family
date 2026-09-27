namespace FamilyHub.Domain.Vaccinations;

/// <summary>Национальный календарь (обязательный, приказ Минздрава №1122н), календарь для взрослых
/// (повторные ревакцинации) и прививки по эпидпоказаниям (добровольные, начинаются только по
/// инициативе пользователя — см. <see cref="VaccinationScheduleCalculator"/>).</summary>
public enum VaccineGroup
{
    National,
    Adult,
    Epidemic,
}

/// <summary>
/// Одна доза серии. Ровно один режим окна:
/// возрастной (<see cref="AgeFrom"/>/<see cref="AgeTo"/> — от даты рождения, для доз национального
/// календаря, где срок известен заранее) либо интервальный (<see cref="IntervalFrom"/>/
/// <see cref="IntervalTo"/> — от даты ПРЕДЫДУЩЕЙ дозы той же серии, для добровольных серий по
/// эпидпоказаниям, где отсчёт идёт не от рождения, а от момента, когда человек фактически начал курс).
/// </summary>
public record VaccineDose(string Label, AgeSpan? AgeFrom, AgeSpan? AgeTo, AgeSpan? IntervalFrom, AgeSpan? IntervalTo)
{
    public bool IsAgeBased => AgeFrom is not null || AgeTo is not null;

    public static VaccineDose ByAge(string label, AgeSpan from, AgeSpan to) => new(label, from, to, null, null);
    public static VaccineDose ByInterval(string label, AgeSpan from, AgeSpan to) => new(label, null, null, from, to);
}

/// <summary>
/// Серия прививок против одной или нескольких инфекций (комбинированные вакцины — одна серия,
/// как корь-краснуха-паротит). Коды стабильны — хранятся в записях (Vaccination.SeriesCode),
/// переименовывать нельзя (как коды HealthMetricCatalog).
/// </summary>
public record VaccineSeries(
    string Code,
    string Name,
    string ShortName,
    VaccineGroup Group,
    IReadOnlyList<string> Diseases,
    IReadOnlyList<VaccineDose> Doses,
    /// <summary>После последней дозы из <see cref="Doses"/> — повторять каждые N лет (АДС-М,
    /// клещевой энцефалит). Отсчёт очередного повтора — от фактической даты предыдущего (реального
    /// или уже сгенерированного виртуального) повтора.</summary>
    int? RepeatEveryYears,
    /// <summary>Сезонная ежегодная прививка (грипп): месяцы, в которые она актуальна в этом году.</summary>
    (int FromMonth, int ToMonth)? SeasonalWindow,
    /// <summary>Перенесённая болезнь закрывает всю серию сразу (корь/краснуха/паротит/ветрянка) —
    /// а не только одну дозу.</summary>
    bool ClosedByDisease,
    string? ReactionHint,
    int ReactionWindowDays,
    IReadOnlyList<string> TradeNames,
    string? About,
    string? Contraindications,
    /// <summary>Название серии для повторов, сгенерированных после последней дозы (см.
    /// RepeatEveryYears) — коклюшный компонент у "dtp" уходит после 14 лет, взрослый повтор
    /// называется иначе (АДС-М). Null — использовать Name/ShortName как есть.</summary>
    string? RepeatName = null,
    string? RepeatShortName = null);

/// <summary>
/// Справочник вакцин. Коды стабильны — переименовывать нельзя (как коды HealthMetricCatalog).
///
/// Источник — приказ Минздрава РФ №1122н (Национальный календарь профилактических прививок) и
/// календарь по эпидемическим показаниям; возрастные окна и интервалы между дозами приведены в
/// разумном приближении для планирования и напоминаний, НЕ являются медицинским назначением —
/// перед реализацией стоит свериться с актуальной редакцией приказа (могла обновиться).
/// </summary>
public static class VaccineCatalog
{
    private static readonly AgeSpan Days3 = AgeSpan.FromDays(3);
    private static readonly AgeSpan Days7 = AgeSpan.FromDays(7);
    private static readonly AgeSpan Months1 = AgeSpan.FromMonths(1);
    private static readonly AgeSpan Months2 = AgeSpan.FromMonths(2);
    private static readonly AgeSpan Months3 = AgeSpan.FromMonths(3);
    private static readonly AgeSpan Months4 = AgeSpan.FromMonths(4);
    private static readonly AgeSpan Months4_5 = new(0, 4, 15);
    private static readonly AgeSpan Months6 = AgeSpan.FromMonths(6);
    private static readonly AgeSpan Months12 = AgeSpan.FromMonths(12);
    private static readonly AgeSpan Months15 = AgeSpan.FromMonths(15);
    private static readonly AgeSpan Months18 = AgeSpan.FromMonths(18);
    private static readonly AgeSpan Years6 = AgeSpan.FromYears(6);
    private static readonly AgeSpan Years7 = AgeSpan.FromYears(7);
    private static readonly AgeSpan Years14 = AgeSpan.FromYears(14);
    private static readonly AgeSpan Years60 = AgeSpan.FromYears(60);
    private static readonly AgeSpan Years100 = AgeSpan.FromYears(100);

    public static readonly IReadOnlyList<VaccineSeries> All =
    [
        new VaccineSeries(
            "hep_b", "Гепатит B", "Гепатит B", VaccineGroup.National, ["Гепатит B"],
            [
                VaccineDose.ByAge("Вакцинация", AgeSpan.Zero, Days3),
                VaccineDose.ByAge("Вторая доза", Months1, Months2),
                VaccineDose.ByAge("Третья доза", Months6, Months12),
            ],
            null, null, false,
            "Лёгкая болезненность в месте укола — обычная реакция в первые 1–2 дня.", 3,
            ["Регевак В", "Эбербиовак НВ", "Энджерикс В"],
            "Защищает от вирусного гепатита B, передающегося через кровь и биологические жидкости.",
            "Острое заболевание или обострение хронического — прививку откладывают до выздоровления."),

        new VaccineSeries(
            "bcg", "Туберкулёз (БЦЖ)", "БЦЖ", VaccineGroup.National, ["Туберкулёз"],
            [
                VaccineDose.ByAge("Вакцинация", Days3, Days7),
                VaccineDose.ByAge("Ревакцинация", Years6, Years7),
            ],
            null, null, false,
            "На месте укола формируется небольшой рубчик в течение нескольких недель — это норма, не реакция.", 30,
            ["БЦЖ-М", "БЦЖ"],
            "Защищает детей от тяжёлых форм туберкулёза. Ревакцинация — только после отрицательной пробы (Манту/диаскинтест).",
            "Иммунодефицит, вес при рождении менее 2000 г — вакцинацию откладывают."),

        new VaccineSeries(
            "dtp", "Дифтерия, коклюш, столбняк", "АКДС/АДС-М", VaccineGroup.National, ["Дифтерия", "Коклюш", "Столбняк"],
            [
                VaccineDose.ByAge("Вакцинация", Months2, Months3),
                VaccineDose.ByAge("Вторая доза", Months4, Months4_5),
                VaccineDose.ByAge("Третья доза", Months6, Months6),
                VaccineDose.ByAge("Ревакцинация 1", Months18, Months18),
                VaccineDose.ByAge("Ревакцинация 2", Years6, Years7),
                VaccineDose.ByAge("Ревакцинация 3 (АДС-М)", Years14, Years14),
            ],
            10, null, false,
            "Возможны недомогание и небольшая температура в первые сутки после прививки.", 3,
            ["Инфанрикс", "Инфанрикс Гекса", "Пентаксим", "АДС-М"],
            "После 6 лет коклюшный компонент убирают (АДС-М) — далее ревакцинация раз в 10 лет пожизненно.",
            "Прогрессирующее заболевание нервной системы — коклюшный компонент противопоказан.",
            RepeatName: "Дифтерия, столбняк (АДС-М)", RepeatShortName: "АДС-М"),

        new VaccineSeries(
            "polio", "Полиомиелит", "Полиомиелит", VaccineGroup.National, ["Полиомиелит"],
            [
                VaccineDose.ByAge("Вакцинация", Months2, Months3),
                VaccineDose.ByAge("Вторая доза", Months4, Months4_5),
                VaccineDose.ByAge("Третья доза", Months6, Months6),
                VaccineDose.ByAge("Ревакцинация 1", Months18, Months18),
                VaccineDose.ByAge("Ревакцинация 2", AgeSpan.FromMonths(20), AgeSpan.FromMonths(20)),
                VaccineDose.ByAge("Ревакцинация 3", Years14, Years14),
            ],
            null, null, false, null, 3,
            ["Полимилекс", "ОПВ", "ИПВ"],
            "Первые дозы — инактивированной вакциной (укол), ревакцинации могут быть живой (капли в рот).", null),

        new VaccineSeries(
            "hib", "Гемофильная инфекция", "Hib", VaccineGroup.National, ["Гемофильная инфекция типа b"],
            [
                VaccineDose.ByAge("Вакцинация", Months2, Months3),
                VaccineDose.ByAge("Вторая доза", Months4, Months4_5),
                VaccineDose.ByAge("Третья доза", Months6, Months6),
                VaccineDose.ByAge("Ревакцинация", Months18, Months18),
            ],
            null, null, false, null, 3, ["Хиберикс", "Акт-ХИБ"],
            "Защищает от менингита, пневмонии и других тяжёлых инфекций, вызванных гемофильной палочкой.", null),

        new VaccineSeries(
            "pneumo_child", "Пневмококковая инфекция", "Пневмококк", VaccineGroup.National, ["Пневмококковая инфекция"],
            [
                VaccineDose.ByAge("Вакцинация", Months2, Months3),
                VaccineDose.ByAge("Вторая доза", Months4, Months4_5),
                VaccineDose.ByAge("Ревакцинация", Months15, Months15),
            ],
            null, null, false, null, 3, ["Превенар 13", "Синфлорикс"], null, null),

        new VaccineSeries(
            "mmr", "Корь, краснуха, паротит", "ККП", VaccineGroup.National, ["Корь", "Краснуха", "Эпидемический паротит"],
            [
                VaccineDose.ByAge("Вакцинация", Months12, Months12),
                VaccineDose.ByAge("Ревакцинация", Years6, Years7),
            ],
            null, null, true,
            "Возможны небольшая температура и лёгкая сыпь на 5–15 день после прививки — это нормальная реакция.", 15,
            ["Вактривир", "M-M-R II", "Приорикс"],
            "Живая вакцина — три инфекции одним уколом.",
            "Иммунодефицит, беременность, аллергия на белок куриного яйца (для некоторых препаратов)."),

        new VaccineSeries(
            "flu", "Грипп", "Грипп", VaccineGroup.Adult, ["Грипп"],
            [VaccineDose.ByAge("Ежегодно", Months6, Months6)],
            null, (9, 11), false,
            "Обычно переносится легко, возможна незначительная болезненность в месте укола.", 3,
            ["Ультрикс Квадри", "Совигрипп", "Флю-М"],
            "Штаммы меняются каждый год — прививаться нужно заново перед каждым сезоном.", null),

        new VaccineSeries(
            "tick_borne_encephalitis", "Клещевой энцефалит", "Клещевой энцефалит", VaccineGroup.Epidemic,
            ["Клещевой энцефалит"],
            [
                VaccineDose.ByAge("Доза 1", AgeSpan.Zero, Years100),
                VaccineDose.ByInterval("Доза 2", Months1, Months3),
                VaccineDose.ByInterval("Доза 3", AgeSpan.FromMonths(9), Months12),
            ],
            3, null, false,
            "В первые дни возможны небольшая температура и покраснение в месте укола.", 7,
            ["Клещ-Э-Вак", "ЭнцеВир", "ФСМЕ-Иммун"],
            "Рекомендуется жителям и выезжающим в эндемичные по клещевому энцефалиту регионы.", null),

        new VaccineSeries(
            "hep_a", "Гепатит A", "Гепатит A", VaccineGroup.Epidemic, ["Гепатит A"],
            [
                VaccineDose.ByAge("Доза 1", AgeSpan.Zero, Years100),
                VaccineDose.ByInterval("Доза 2", Months6, AgeSpan.FromMonths(18)),
            ],
            null, null, false, null, 3,
            ["Альгавак М", "Хаврикс"],
            "По эпидпоказаниям и перед поездками в регионы с высоким риском гепатита A.", null),

        new VaccineSeries(
            "varicella", "Ветряная оспа", "Ветрянка", VaccineGroup.Epidemic, ["Ветряная оспа"],
            [
                VaccineDose.ByAge("Доза 1", AgeSpan.Zero, Years100),
                VaccineDose.ByInterval("Доза 2", Months3, Months3),
            ],
            null, null, true,
            "Возможна лёгкая сыпь, напоминающая ветрянку, через 1–3 недели после прививки.", 21,
            ["Варилрикс"], null, null),

        new VaccineSeries(
            "meningo", "Менингококковая инфекция", "Менингококк", VaccineGroup.Epidemic, ["Менингококковая инфекция"],
            [VaccineDose.ByAge("Доза 1", AgeSpan.Zero, Years100)],
            null, null, false, null, 3, ["Менактра", "Менвео"], null, null),

        new VaccineSeries(
            "rotavirus", "Ротавирусная инфекция", "Ротавирус", VaccineGroup.Epidemic, ["Ротавирусная инфекция"],
            [
                VaccineDose.ByAge("Доза 1", Months2, Months3),
                VaccineDose.ByInterval("Доза 2", Months1, Months2),
                VaccineDose.ByInterval("Доза 3", Months1, Months2),
            ],
            null, null, false, null, 3, ["РотаТек"], "Капли — защищает от тяжёлой ротавирусной диареи у младенцев.", null),

        new VaccineSeries(
            "hpv", "Вирус папилломы человека", "ВПЧ", VaccineGroup.Epidemic, ["ВПЧ"],
            [
                VaccineDose.ByAge("Доза 1", AgeSpan.FromYears(9), Years100),
                VaccineDose.ByInterval("Доза 2", Months6, AgeSpan.FromMonths(15)),
            ],
            null, null, false, null, 3, ["Гардасил", "Церварикс"], null, null),

        new VaccineSeries(
            "pneumo_adult", "Пневмококк (после 60 лет)", "Пневмококк 60+", VaccineGroup.Epidemic, ["Пневмококковая инфекция"],
            [VaccineDose.ByAge("Доза 1", Years60, Years100)],
            null, null, false, null, 3, ["Пневмовакс 23", "Превенар 13"],
            "Рекомендуется людям старше 60 лет и с хроническими заболеваниями.", null),
    ];

    public static VaccineSeries? Find(string? code) => All.FirstOrDefault(s => s.Code == code);
}
