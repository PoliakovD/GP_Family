using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;

namespace FamilyHub.Api.Features.Admin;

public record TrustedDomainDto(Guid Id, string Domain, int Rank, bool IsEnabled);

public record AddTrustedDomainRequest(WebSearchTopic Topic, string Domain);

public record SetTrustedDomainEnabledRequest(bool IsEnabled);

public record ReorderTrustedDomainsRequest(WebSearchTopic Topic, List<Guid> OrderedIds);

/// <summary>Строка списка кэша сырых результатов поиска — без самих сниппетов (превью), для
/// таблицы в админке. Specimen и Units — только для Topic=LabAnalyte. NormalizedName — ключ кэша (свёрнутый, по нему
/// задачи находят строку), DisplayName — название для людей («СРБ»), null — ещё не известно.
/// Units: null — не определены, "" — проверено, единиц нет, иначе «г/л; ммоль/л».</summary>
public record SearchCacheRowDto(
    Guid Id, string NormalizedName, string? Specimen, string Provider,
    DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter, int SnippetCount,
    string? DisplayName = null, string? Units = null);

public record SearchCacheListResponse(List<SearchCacheRowDto> Rows, int Total);

/// <summary>Один сниппет с уже вычисленным итоговым решением (Enabled) — точно то, что реально
/// уйдёт/не уйдёт суммаризатору при следующем прогоне обогащения с текущими настройками.</summary>
public record SearchCacheSnippetDto(
    string Title, string Url, string Text, string? Domain, bool IsTrustedByDomain, bool? Override, bool Enabled,
    SnippetOrigin Origin = SnippetOrigin.Auto, string? Kind = null, string? Note = null, bool Pinned = false);

/// <summary>DisplayName/Units — см. SearchCacheRowDto.</summary>
public record SearchCacheDetailDto(
    Guid Id, string NormalizedName, string? Specimen, string Provider,
    DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter, List<SearchCacheSnippetDto> Snippets,
    string? DisplayName = null, string? Units = null);

public record SetSnippetOverrideRequest(WebSearchTopic Topic, string Url, bool? Enabled);

/// <summary>Один сниппет на запись — форма ввода, без вычисленных Enabled/IsTrustedByDomain
/// (это read-only проекция для отображения, см. SearchCacheSnippetDto).</summary>
/// <summary>Origin/Kind/Note/Pinned необязательны (старый редактор кэша их не шлёт): без Origin сниппет — Auto.</summary>
public record SearchCacheSnippetInput(
    string Title, string Url, string Text,
    SnippetOrigin? Origin = null, string? Kind = null, string? Note = null, bool? Pinned = null);

/// <summary>Полное редактирование строки кэша (§ полного CRUD кэша) — Snippets заменяет
/// SnippetsJson целиком (тот же приём, что payload-редактор справочника — весь список разом,
/// не позиционные add/edit/remove). Provider — null, чтобы не трогать текущее значение.
/// DisplayName: null — не трогать, "" — убрать. Units (только показатели): null — не трогать, иначе список через
/// «;»/«,» ("" — проверено, единиц нет); ResetUnits — снова «не определено», строку подхватит фоновая разметка.</summary>
public record UpdateSearchCacheRequest(
    WebSearchTopic Topic, string? Provider, List<SearchCacheSnippetInput> Snippets,
    string? DisplayName = null, string? Units = null, bool ResetUnits = false);
