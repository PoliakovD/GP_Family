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
    public string Model { get; set; } = "qwen3.5-9b-uncensored-hauhaucs-aggressive";

    /// <summary>Таймаут запроса — локальный vision-инференс на нескольких фото не быстрый.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}
