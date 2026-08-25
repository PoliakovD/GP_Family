using System.Text.RegularExpressions;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Проверка payload на признаки персонального контекста перед записью в любой обезличенный
/// справочник (kb.global_medications_kb, kb.global_lab_analytes_kb) — общая для
/// <see cref="KbWriter"/> и <c>LabAnalyteKbWriter</c> (ветка medicalrecords). Вынесена сюда при
/// добавлении второго writer'а: до этого правило жило только внутри KbWriter — дублировать
/// пять regex'ов под второй справочник означало бы разойтись при следующей правке одного из них.
/// Дополняет структурную изоляцию (обе KB-модели без персональных полей, см. KbIsolationGuardTests) —
/// защита на случай, если модель случайно подмешает в текст что-то похожее на идентификатор.
/// </summary>
public static class KbIsolationGuard
{
    private static readonly Regex GuidPattern = new(
        @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled);

    /// <summary>7+ подряд идущих цифр — телефон/паспорт/номер карты, не имеет отношения к
    /// обезличенному знанию о препарате/показателе.</summary>
    private static readonly Regex LongDigitsPattern = new(@"\d{7,}", RegexOptions.Compiled);

    /// <summary>То же множество ключевых слов, что и KbIsolationGuardTests.PersonalContextPattern —
    /// один инвариант, проверяемый на двух уровнях (структура модели + значения payload).</summary>
    private static readonly Regex PersonalKeywordPattern = new(
        @"\b(UserId|FamilyId|Person|Owner|Telegram|Email|Phone|Member)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Первая найденная подозрительная строка среди кандидатов, либо null, если payload чист.</summary>
    public static string? FindViolation(IEnumerable<string?> candidates)
    {
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
