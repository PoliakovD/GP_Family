namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Один результат внешнего веб-поиска — сниппет выдачи, не полная страница (осознанно
/// не скрейпим: меньше egress, не ломается от смены вёрстки источника).</summary>
public record WebSnippet(string Title, string Url, string Text);

/// <summary>
/// Какой конвейер обогащения запрашивает поиск — провайдер выбирает по этому значению список
/// доверенных доменов (EnrichmentOptions.TrustedDomains vs AnalyteTrustedDomains) и формулировку
/// запроса: реестры лекарств (vidal.ru, rlsnet.ru) бесполезны для референсных диапазонов лаб.
/// показателей, и наоборот (ветка medicalrecords — справочник kb.global_lab_analytes_kb).
/// </summary>
public enum WebSearchTopic { Medication, LabAnalyte }

/// <summary>
/// Абстракция внешнего поиска для обогащения справочников (этап 4 — препараты, ветка
/// medicalrecords — лабораторные показатели, ADR-0005). Реализация подключается конфигом
/// (Enrichment:Provider), не кодом — по образцу INotificationSender-фан-аута в этом проекте.
/// Наружу должно уходить ТОЛЬКО нормализованное название (препарата или показателя) — без
/// user/family-контекста (см. ADR-0001, п.3).
/// </summary>
public interface IMedicationSearchProvider
{
    /// <summary>Имя провайдера — попадает в Source обогащённой записи для прослеживаемости знания.</summary>
    string Name { get; }

    Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication, CancellationToken ct = default);
}
