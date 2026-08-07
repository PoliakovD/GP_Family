namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Один результат внешнего веб-поиска — сниппет выдачи, не полная страница (осознанно
/// не скрейпим: меньше egress, не ломается от смены вёрстки источника).</summary>
public record WebSnippet(string Title, string Url, string Text);

/// <summary>
/// Абстракция внешнего поиска для обогащения справочника препаратов (этап 4, ADR-0005).
/// Реализация подключается конфигом (Enrichment:Provider), не кодом — по образцу
/// INotificationSender-фан-аута в этом проекте. Наружу должно уходить
/// ТОЛЬКО нормализованное название препарата — без user/family-контекста (см. ADR-0001, п.3).
/// </summary>
public interface IMedicationSearchProvider
{
    /// <summary>Имя провайдера — попадает в GlobalMedicationKb.Source для прослеживаемости знания.</summary>
    string Name { get; }

    Task<IReadOnlyList<WebSnippet>> SearchAsync(string normalizedName, CancellationToken ct = default);
}
