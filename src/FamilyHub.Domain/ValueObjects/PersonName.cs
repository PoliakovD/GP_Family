using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.ValueObjects;

/// <summary>Формат отображения ФИО, зависящий от ширины экрана клиента.</summary>
public enum PersonNameStyle
{
    /// <summary>«Иванов Иван Иванович» (десктоп, ≥1024px).</summary>
    Full,

    /// <summary>«Иванов Иван И.» (планшет, 640–1023px).</summary>
    ShortPatronymic,

    /// <summary>«Иванов И.И.» (телефон, &lt;640px).</summary>
    Initials,
}

/// <summary>
/// Правила ФИО, общие для профиля User (identity rework) и FamilyDependent — единый источник
/// истины, т.к. форматирование нужно и Api (списки участников семьи), и Infrastructure
/// (ReminderScanJob формирует текст оповещения о ДР). См. .claude/patterns/backend.md
/// («Разделяемая политика формата — в Domain, не в сервисе»).
///
/// Отчество необязательно (не у всех есть — иностранцы, некоторые национальности): все стили
/// корректно схлопываются без него. Фамилия и Имя — обязательны везде, где используется этот тип.
/// </summary>
public static class PersonName
{
    public const int MaxPartLength = 100;
    private const int MaxAgeYears = 120;

    /// <summary>Непустая строка (после Trim) не длиннее <see cref="MaxPartLength"/>.</summary>
    public static bool IsValidPart(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxPartLength;

    /// <summary>Отчество — необязательно, но если задано, тоже ограничено по длине.</summary>
    public static bool IsValidOptionalPart(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length <= MaxPartLength;

    /// <summary>
    /// Профиль User считается заполненным, когда есть все пять полей (отчество не входит —
    /// оно опционально по построению). Используется гардами (authGuard-эквивалент profileGuard
    /// на фронте, TelegramBindingService.profileRequired) — не NOT NULL на колонках, потому что
    /// строка User легитимно существует до заполнения профиля (создаётся сразу после email-OTP,
    /// ФИО собирается отдельным экраном).
    /// </summary>
    public static bool IsCompleteProfile(string? lastName, string? firstName, DateOnly? birthDate, Gender? gender) =>
        IsValidPart(lastName) && IsValidPart(firstName) && birthDate is not null && gender is not null
        && IsValidBirthDate(birthDate.Value);

    /// <summary>Не в будущем и не старше <see cref="MaxAgeYears"/> лет — общая проверка для
    /// профиля User и, отдельным путём, FamilyDependent.BirthDate (которое дополнительно
    /// допускает null — "дата неизвестна").</summary>
    public static bool IsValidBirthDate(DateOnly value)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return value <= today && value >= today.AddYears(-MaxAgeYears);
    }

    /// <summary>
    /// Форматирует ФИО под указанный стиль. Ожидает уже провалидированные (IsValidPart)
    /// lastName/firstName — middleName может быть null/пустым независимо от стиля.
    /// </summary>
    public static string Format(string? lastName, string? firstName, string? middleName, PersonNameStyle style)
    {
        var last = lastName?.Trim() ?? string.Empty;
        var first = firstName?.Trim() ?? string.Empty;
        var middle = middleName?.Trim();
        var hasMiddle = !string.IsNullOrEmpty(middle);

        return style switch
        {
            // Full и ShortPatronymic совпадают без отчества — не разводить на два "if" в каждом
            // потребителе, схлопывание уже учтено здесь.
            PersonNameStyle.Full => hasMiddle ? $"{last} {first} {middle}" : $"{last} {first}",
            PersonNameStyle.ShortPatronymic => hasMiddle ? $"{last} {first} {Initial(middle!)}." : $"{last} {first}",
            PersonNameStyle.Initials => hasMiddle
                ? $"{last} {Initial(first)}.{Initial(middle!)}."
                : $"{last} {Initial(first)}.",
            _ => throw new ArgumentOutOfRangeException(nameof(style), style, null),
        };
    }

    /// <summary>
    /// Как <see cref="Format"/>, но с фолбэком для незавершённого профиля — узел (уведомления,
    /// текст письма) не должен показывать пустую строку/одинокий пробел, если LastName/FirstName
    /// ещё не заполнены (легитимное промежуточное состояние User, см. Entities.User).
    /// </summary>
    public static string FormatOrDefault(
        string? lastName, string? firstName, string? middleName, PersonNameStyle style, string fallback) =>
        IsValidPart(lastName) && IsValidPart(firstName) ? Format(lastName, firstName, middleName, style) : fallback;

    private static string Initial(string part) => part.Length > 0 ? part[..1].ToUpperInvariant() : string.Empty;
}
