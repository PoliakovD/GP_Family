namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Настройки подключения к реальному объектному хранилищу MinIO (этап 2 п.9 брифа).
/// По разделу 9: прод-инстанс — на домашнем ПК пользователя, доступен только по presigned URL,
/// без постоянных прямых ссылок.
/// </summary>
public class MinioOptions
{
    public const string SectionName = "Minio";

    /// <summary>Хост:порт MinIO, например "localhost:9000" или "minio.example.com".</summary>
    public string Endpoint { get; set; } = "localhost:9000";

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
