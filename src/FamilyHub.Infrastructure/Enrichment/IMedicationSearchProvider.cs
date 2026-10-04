using System.Text.Json.Serialization;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Один результат внешнего веб-поиска — сниппет выдачи, не полная страница (осознанно
/// не скрейпим: меньше egress, не ломается от смены вёрстки источника). Провайдер больше НЕ
/// фильтрует по доверенным доменам (пересборка enrich-пайплайна, см. EnrichmentSnippetFilter) —
/// сюда попадают ВСЕ результаты поиска, включая недоверенные; фильтрация — на процессоре, по
/// БД-списку доверенных доменов (EnrichmentTrustedDomain), управляемому через админку.</summary>
/// <param name="Origin">Auto — результат платного/кэшируемого поиска; Manual — добавлен администратором
/// вручную (ADR-0018) и НЕ перезаписывается автообновлением кэша и чисткой.</param>
/// <param name="Kind">null — обычный веб-результат; "manual-quote" — короткая цитата со ссылкой на
/// источник; "expert-knowledge" — знание эксперта без URL (Url — служебный expert://admin/&lt;id&gt;).</param>
/// <param name="Note">Заметка админа к ручному сниппету — в суммаризатор не передаётся.</param>
/// <param name="Pinned">Закреплён: всегда входит в набор для суммаризации, до отсечки MaxSnippets.</param>
public record WebSnippet(
    string Title, string Url, string Text,
    SnippetOrigin Origin = SnippetOrigin.Auto, string? Kind = null, string? Note = null, bool Pinned = false);

/// <summary>Происхождение сниппета в кэше поиска. Сериализуется строкой — чтобы чистка/фильтры по
/// SnippetsJson (LIKE) не зависели от числового значения enum'а.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnippetOrigin
{
    Auto = 0,
    Manual = 1,
}

/// <summary>Виды ручных сниппетов (WebSnippet.Kind) и служебный URL знания эксперта.</summary>
public static class SnippetKinds
{
    public const string ManualQuote = "manual-quote";
    public const string ExpertKnowledge = "expert-knowledge";

    /// <summary>У знания эксперта нет настоящего URL — берём служебный (он же ключ для overrides и
    /// уникален на сниппет), чтобы вся остальная механика (override/pin/удаление по URL) работала одинаково.</summary>
    public const string ExpertUrlPrefix = "expert://admin/";

    /// <summary>Подпись источника в Source записи справочника для знания эксперта.</summary>
    public const string ExpertSourceLabel = "Эксперт: админ";

    public static bool IsExpertUrl(string? url) =>
        url is not null && url.StartsWith(ExpertUrlPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Предельная длина цитаты/знания — код принимает короткую выдержку, не целую статью
    /// (авторские права и объём контекста суммаризатора).</summary>
    public const int MaxManualTextLength = 800;
}

/// <summary>Откуда пришёл вызов SearchAsync — только для WebSearchCallLog.JobKind/JobId (переход
/// из строки аудит-лога в карточку задачи в /admin/pipeline), никак не влияет на сам поиск.
/// JobKind — вид задачи строкой ("LabAnalyteEnrichment"/"MedicationEnrichment"/"VisitMedicationEnrichment",
/// "SearchCacheWarmup" — см. WebSearchCallLog.JobKind).</summary>
public record WebSearchCallContext(string JobKind, Guid JobId);

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
    /// общий запрос. callContext — см. WebSearchCallContext, null допустим везде (тесты, будущие
    /// вызовы без известной задачи) — тогда лог пишется без ссылки на задачу.</summary>
    Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        string? specimenDisplayName = null, CancellationToken ct = default,
        WebSearchCallContext? callContext = null);
}
