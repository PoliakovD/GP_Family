namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Одна настройка из env/appsettings для read-only вида «Настройки → Конфиг env». <c>Key</c> —
/// путь конфигурации через двоеточие (<c>Enrichment:Provider</c>); UI показывает его в форме имени
/// переменной окружения (<c>Enrichment__Provider</c>). <c>Source</c> — где значение реально
/// задано: <c>env</c> | <c>appsettings</c> | <c>cli</c> | <c>other</c> | <c>default</c> (не задано
/// нигде — действует значение по умолчанию из кода).
///
/// Секрет (<c>IsSecret</c>) по построению НЕ несёт значения: <c>Value</c> всегда null, наружу уходит
/// только <c>IsSet</c> — задан ли он вообще (см. AdminConfigService.SectionBuilder.Secret).
/// </summary>
public record ConfigItemDto(string Key, string? Value, bool IsSecret, bool IsSet, string Source);

public record ConfigSectionDto(string Name, string Title, IReadOnlyList<ConfigItemDto> Items);

public record AdminConfigDto(IReadOnlyList<ConfigSectionDto> Sections);
