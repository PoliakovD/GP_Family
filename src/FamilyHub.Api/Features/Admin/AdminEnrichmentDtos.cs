using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;

namespace FamilyHub.Api.Features.Admin;

public record TrustedDomainDto(Guid Id, string Domain, int Rank, bool IsEnabled);

public record AddTrustedDomainRequest(WebSearchTopic Topic, string Domain);

public record SetTrustedDomainEnabledRequest(bool IsEnabled);

public record ReorderTrustedDomainsRequest(WebSearchTopic Topic, List<Guid> OrderedIds);

/// <summary>Строка списка кэша сырых результатов поиска — без самих сниппетов (превью), для
/// таблицы в админке. Specimen — только для Topic=LabAnalyte.</summary>
public record SearchCacheRowDto(
    Guid Id, string NormalizedName, string? Specimen, string Provider,
    DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter, int SnippetCount);

public record SearchCacheListResponse(List<SearchCacheRowDto> Rows, int Total);

/// <summary>Один сниппет с уже вычисленным итоговым решением (Enabled) — точно то, что реально
/// уйдёт/не уйдёт суммаризатору при следующем прогоне обогащения с текущими настройками.</summary>
public record SearchCacheSnippetDto(
    string Title, string Url, string Text, string? Domain, bool IsTrustedByDomain, bool? Override, bool Enabled,
    SnippetOrigin Origin = SnippetOrigin.Auto, string? Kind = null, string? Note = null, bool Pinned = false);

public record SearchCacheDetailDto(
    Guid Id, string NormalizedName, string? Specimen, string Provider,
    DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter, List<SearchCacheSnippetDto> Snippets);

public record SetSnippetOverrideRequest(WebSearchTopic Topic, string Url, bool? Enabled);

/// <summary>Один сниппет на запись — форма ввода, без вычисленных Enabled/IsTrustedByDomain
/// (это read-only проекция для отображения, см. SearchCacheSnippetDto).</summary>
/// <summary>Origin/Kind/Note/Pinned необязательны (старый редактор кэша их не шлёт): без Origin сниппет — Auto.</summary>
public record SearchCacheSnippetInput(
    string Title, string Url, string Text,
    SnippetOrigin? Origin = null, string? Kind = null, string? Note = null, bool? Pinned = null);

/// <summary>Полное редактирование строки кэша (§ полного CRUD кэша) — Snippets заменяет
/// SnippetsJson целиком (тот же приём, что payload-редактор справочника — весь список разом,
/// не позиционные add/edit/remove). Provider — null, чтобы не трогать текущее значение.</summary>
public record UpdateSearchCacheRequest(WebSearchTopic Topic, string? Provider, List<SearchCacheSnippetInput> Snippets);
