namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Абстракция источника дней рождения для глобального поиска (используется SearchService,
/// Modules.Medical) — без прямой зависимости от Modules.Birthdays: оба модуля сознательно
/// зависят только от Domain/Infrastructure и не ссылаются друг на друга (см.
/// AddMedicalModule/AddBirthdayModule). Реализация — BirthdaySearchSource в Modules.Birthdays,
/// регистрируется через AddBirthdayModule и резолвится сюда DI-контейнером на уровне API.
/// </summary>
public interface IBirthdaySearchSource
{
    Task<List<BirthdaySearchHit>> SearchAsync(Guid userId, string query, int limit, CancellationToken ct = default);
}

/// <summary>PersonName уже расшифрован (см. ADR-0002/ADR-0003 — материализация через EF-конвертер).</summary>
public record BirthdaySearchHit(
    Guid Id, Guid FamilyId, string FamilyName, string PersonName, DateOnly Date, double Score);
