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

    /// <summary>По умолчанию None (без размышлений) — самый быстрый ответ; конфигурируется через
    /// env LmStudio__Reasoning=none|minimal|medium|maximum.</summary>
    public LmStudioReasoning Reasoning { get; set; } = LmStudioReasoning.None;
}
