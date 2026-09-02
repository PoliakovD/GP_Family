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

    /// <summary>specimenDisplayName — только для WebSearchTopic.LabAnalyte (пересборка
    /// enrich-пайплайна): готовый текст из справочника источников (GlobalSpecimenKb.DisplayName,
    /// например "кровь" или "ЭКГ"), делает сырой запрос информативнее ("натрий в крови", не
    /// просто "натрий") — см. AnalyteSearchQueryBuilder. Никакой классификации на стороне
    /// провайдера — источник уже пришёл готовой строкой из справочника, код здесь её не
    /// интерпретирует. Null у медикаментов и у анализов без определённого источника — прежний,
    /// общий запрос.</summary>
    Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        string? specimenDisplayName = null, CancellationToken ct = default);
}
