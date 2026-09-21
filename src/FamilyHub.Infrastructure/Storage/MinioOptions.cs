using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Настройки подключения к реальному объектному хранилищу MinIO (этап 2 п.9 брифа).
/// По разделу 9: прод-инстанс — на домашнем ПК пользователя, доступен только по presigned URL,
/// без постоянных прямых ссылок.
/// </summary>
public class MinioOptions
{
    public const string SectionName = "Minio";

    /// <summary>Хост:порт MinIO, например "localhost:9000" или "minio.example.com". Без дефолта
    /// (при cleanup-рефакторинге убран прежний "localhost:9000") — MinIO единственная реализация
    /// IFileStorage везде, включая Development (см. class doc AddFamilyHubFileStorage), и во всех
    /// реальных окружениях (docker-compose/прод/тесты) значение задаётся явно; дефолт создавал
    /// иллюзию, что пустая конфигурация — рабочий случай, хотя Endpoint здесь обязателен так же,
    /// как AccessKey/SecretKey ниже, см. MinioOptionsValidator.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Бакет, в котором хранятся все вложения FamilyHub.</summary>
    public string Bucket { get; set; } = "familyhub";

    /// <summary>Использовать HTTPS при обращении к MinIO.</summary>
    public bool UseSsl { get; set; }

    /// <summary>
    /// Если задан — хост, который подставляется в presigned URL вместо <see cref="Endpoint"/>
    /// (например, когда MinIO виден изнутри сети по одному адресу, а клиентам — по другому,
    /// через обратный прокси/туннель). Если null — используется Endpoint как есть.
    /// </summary>
    public string? PublicEndpoint { get; set; }
}

/// <summary>
/// Fail-fast при старте хоста (cleanup-рефакторинг — заменяет прежнюю ручную проверку сырых строк
/// конфига в AddFamilyHubFileStorage, см. её class doc): раньше был переключатель FileStorage:
/// Provider=Local|Minio, запуск из IDE тихо писал медицинские сканы на диск мимо объектного
/// хранилища, и этот путь никогда не проверялся. Без этой проверки ошибка всплыла бы только при
/// первой загрузке файла.
/// </summary>
public class MinioOptionsValidator : IValidateOptions<MinioOptions>
{
    public ValidateOptionsResult Validate(string? name, MinioOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint)
            || string.IsNullOrWhiteSpace(options.AccessKey)
            || string.IsNullOrWhiteSpace(options.SecretKey))
        {
            return ValidateOptionsResult.Fail(
                "Minio:Endpoint/AccessKey/SecretKey не заданы (env Minio__Endpoint/Minio__AccessKey/" +
                "Minio__SecretKey) — хранилище вложений обязательно, в т.ч. в Development (см. docker-compose.yml).");
        }
        return ValidateOptionsResult.Success;
    }
}
