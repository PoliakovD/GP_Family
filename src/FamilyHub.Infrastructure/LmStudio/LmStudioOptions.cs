namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Уровень "размышлений" модели (Qwen3 thinking-режим) — один общий дефолт для OCR и суммаризации
/// (см. LmStudioJsonClient). Гарантированно различается только None от любого другого уровня:
/// None шлёт "/no_think" и enable_thinking=false — reasoning-блок не генерируется вовсе, самый
/// быстрый ответ. Low/Medium/High шлют "/think", enable_thinking=true и best-effort-подсказки
/// объёма рассуждения (chat_template_kwargs.thinking_budget, reasoning_effort) — конкретная
/// глубина размышления зависит от того, поддерживает ли ИМЕННО ЗАГРУЖЕННАЯ модель/чат-шаблон эти
/// подсказки; если нет, все три уровня выше None ведут себя одинаково ("думает, сколько сочтёт
/// нужным"), что не хуже нынешнего поведения без этой настройки.
/// </summary>
public enum LmStudioThinkingLevel
{
    /// <summary>Без размышлений — "/no_think", самый быстрый и дешёвый по токенам ответ.</summary>
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Конфигурация секции "LmStudio" в appsettings — локальный OpenAI-совместимый сервер с vision-LLM.</summary>
public class LmStudioOptions
{
    public const string SectionName = "LmStudio";

    /// <summary>
    /// Базовый адрес LM Studio. По умолчанию localhost — для запуска API вне Docker.
    /// В docker-compose переопределяется на http://host.docker.internal:1234, т.к. api
    /// крутится в контейнере, а LM Studio — на хосте.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:1234";

    /// <summary>Идентификатор модели, передаваемый в поле "model" запроса chat/completions.</summary>
    public string Model { get; set; } = "qwen3.5-9b-uncensored-hauhaucs-aggressive";

    /// <summary>Таймаут запроса — локальный vision-инференс на нескольких фото не быстрый,
    /// а при ThinkingLevel выше None размышление может занять заметно дольше.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>По умолчанию None (без размышлений) — самый быстрый ответ; конфигурируется через
    /// env LmStudio__ThinkingLevel=0..3 (0=нет, 1=минимум, 2=среднее, 3=максимум).</summary>
    public LmStudioThinkingLevel ThinkingLevel { get; set; } = LmStudioThinkingLevel.None;
}
