using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.LmStudio;

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
    public int TimeoutSeconds { get; set; } = 1000;

    /// <summary>Таймаут для "дешёвых" вызовов — короткий текстовый промпт или OCR по нескольким фото
    /// (упаковка лекарства/сертификат прививки, до 5-8 штук за раз), без многостраничного
    /// распознавания целого документа (классификация, генерация названия, коррекция имён, судья
    /// качественной нормы и т.п., см. вызывающих shortTimeout: true у ExtractJsonAsync). При
    /// "молчащем" туннеле (пакеты теряются, а не отбиваются) держать такой вызов до полного
    /// <see cref="TimeoutSeconds"/> (по умолчанию — 1000 c, TECH_DEBT.md #6/#7) нет смысла: он либо
    /// ответит за секунды-десятки секунд, либо не ответит вовсе.</summary>
    public int ShortCallTimeoutSeconds { get; set; } = 120;

    /// <summary>По умолчанию None (без размышлений) — самый быстрый ответ; конфигурируется через
    /// env LmStudio__Reasoning=none|minimal|medium|maximum.</summary>
    public LmStudioReasoning Reasoning { get; set; } = LmStudioReasoning.None;
}
