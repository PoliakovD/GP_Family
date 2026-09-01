using FamilyHub.Domain.Enums;

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
    string Title, string Url, string Text, string? Domain, bool IsTrustedByDomain, bool? Override, bool Enabled);

public record SearchCacheDetailDto(
    Guid Id, string NormalizedName, string? Specimen, string Provider,
    DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter, List<SearchCacheSnippetDto> Snippets);

public record SetSnippetOverrideRequest(WebSearchTopic Topic, string Url, bool? Enabled);
