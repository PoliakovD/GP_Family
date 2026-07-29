namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// In-memory полнотекстовый поиск с русской морфологией и устойчивостью к опечаткам OCR —
/// применяется там, где Postgres-FTS невозможен физически (зашифрованные at-rest поля, ADR-0002/
/// ADR-0003): данные уже расшифрованы в памяти сервиса, дальше матчинг идёт здесь. Живёт в
/// Infrastructure (не в конкретном модуле), т.к. используется несколькими независимыми модулями
/// (Modules.Medical — медкарты, Modules.Birthdays — дни рождения), которые сознательно не
/// ссылаются друг на друга (см. AddMedicalModule/AddBirthdayModule).
/// </summary>
public interface IRussianTextSearcher
{
    /// <summary>
    /// Релевантность <paramref name="text"/> запросу <paramref name="query"/>: 0 — не совпадает,
    /// иначе (0..1] — тем выше, чем точнее совпадение. AND-семантика по словам запроса (как
    /// <c>plainto_tsquery</c> в Postgres) — все значимые слова запроса должны найтись в тексте,
    /// морфологически или с учётом опечатки.
    /// </summary>
    double Score(string? text, string? query);
}
