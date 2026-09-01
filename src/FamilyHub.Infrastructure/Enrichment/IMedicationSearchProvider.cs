using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Один результат внешнего веб-поиска — сниппет выдачи, не полная страница (осознанно
/// не скрейпим: меньше egress, не ломается от смены вёрстки источника). Провайдер больше НЕ
/// фильтрует по доверенным доменам (пересборка enrich-пайплайна, см. EnrichmentSnippetFilter) —
/// сюда попадают ВСЕ результаты поиска, включая недоверенные; фильтрация — на процессоре, по
/// БД-списку доверенных доменов (EnrichmentTrustedDomain), управляемому через админку.</summary>
public record WebSnippet(string Title, string Url, string Text);

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

    /// <summary>specimen — только для WebSearchTopic.LabAnalyte (пересборка enrich-пайплайна):
    /// делает сырой запрос информативнее ("натрий в моче", не просто "натрий") — см. реализации.
    /// Unknown у медикаментов и у анализов без определённого биоматериала — прежний, общий запрос.</summary>
    Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        SpecimenType specimen = SpecimenType.Unknown, CancellationToken ct = default);
}
