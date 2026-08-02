namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Уровень "размышлений" модели — один общий переключатель для OCR и суммаризации (см.
/// LmStudioJsonClient). Перепробовано и отброшено curl'ом напрямую против LM Studio, по очереди:
/// - "/no_think" в тексте сообщения + chat_template_kwargs.enable_thinking — на кастомном пресете
///   Qwen3.5 не работают вовсе (модель всё равно думала ~570 токенов);
/// - top-level "reasoning_effort" (none/minimal/low/medium/high/xhigh) — у Qwen3.5 из всей
///   шкалы реально маппится только "none" (остальное — откат на "on" с варнингом в логах LM
///   Studio), а у bonsai-27b (Q1_0, arch qwen35) шкала вообще не коррелирует с реальной глубиной
///   рассуждения ("none" неожиданно давал БОЛЬШЕ reasoning-токенов, чем "maximum").
/// Т.е. ни один API-параметр LM Studio не даёт предсказуемого контроля глубины на разных
/// моделях/квантизациях. Поэтому уровень задаётся ЕСТЕСТВЕННЫМ ЯЗЫКОМ прямо в системном
/// промпте — канал рассуждений остаётся структурно включённым всегда (никакого reasoning_effort
/// в запросе), а модель сама подстраивает объём размышлений под словесную инструкцию.
/// </summary>
public enum LmStudioReasoning
{
    /// <summary>Без рассуждений — просим модель отвечать сразу, без промежуточных шагов
    /// (по умолчанию, самый быстрый ответ).</summary>
    None = 0,

    /// <summary>Минимум рассуждений — только по необходимости.</summary>
    Minimal = 1,

    /// <summary>Умеренные рассуждения — по существу, без лишних деталей.</summary>
    Medium = 2,

    /// <summary>Максимально подробные рассуждения с проверкой каждого шага.</summary>
    Maximum = 3,
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
    public string Model { get; set; } = "prism-ml/bonsai-27b";

    /// <summary>Таймаут запроса — локальный vision-инференс на нескольких фото не быстрый,
    /// а при более высоком уровне размышлений может занять заметно дольше.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>По умолчанию None (без размышлений) — самый быстрый ответ; конфигурируется через
    /// env LmStudio__Reasoning=none|minimal|medium|maximum.</summary>
    public LmStudioReasoning Reasoning { get; set; } = LmStudioReasoning.None;
}
