using FamilyHub.Domain.Entities;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Абстракция над <see cref="EnrichmentRequestService"/> — MedicationService зависит от интерфейса,
/// не от конкретного класса, потому что реализация ходит raw SQL к Postgres-специфичным функциям
/// (tsvector/similarity, см. KbLookupService) и не может исполняться против SQLite в unit-тестах
/// (SqliteTestBase). Тесты MedicationService подставляют no-op заглушку через NSubstitute.
/// </summary>
public interface IEnrichmentRequestService
{
    Task RequestAsync(Medication medication, Guid userId, CancellationToken ct = default);

    /// <summary>Ручной запрос («Уточнить в справочнике») — в отличие от RequestAsync не прерывается
    /// на существующем Hit (пользователь хочет принудительного повторного обогащения).</summary>
    Task<EnrichmentRefreshOutcome> RequestRefreshAsync(Medication medication, Guid userId, CancellationToken ct = default);
}
