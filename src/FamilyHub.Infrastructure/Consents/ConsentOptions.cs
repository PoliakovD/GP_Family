namespace FamilyHub.Infrastructure.Consents;

/// <summary>Настройки согласий (секция "Consents").</summary>
public class ConsentOptions
{
    public const string SectionName = "Consents";

    /// <summary>
    /// Актуальная версия текста согласия. Меняется при каждом изменении текста
    /// (история версий — git, тексты в src/FamilyHub.Api/Legal). Пользователи с согласием
    /// на старую версию должны подтвердить новую.
    /// </summary>
    public string CurrentVersion { get; set; } = "2026-07-27";
}
