namespace FamilyHub.Infrastructure.Storage;

/// <summary>Конфигурация секции "LocalFileStorage" в appsettings.</summary>
public class LocalFileStorageOptions
{
    public const string SectionName = "LocalFileStorage";

    /// <summary>Каталог на диске, куда складываются файлы (временная замена MinIO).</summary>
    public string RootPath { get; set; } = "App_Data/uploads";

    /// <summary>Базовый публичный путь, под которым отдаются файлы по подписанной ссылке.</summary>
    public string PublicBasePath { get; set; } = "/local-files";

    /// <summary>Секрет для подписи pre-signed URL (HMAC). В проде заменяется на политику MinIO.</summary>
    public string SigningKey { get; set; } = "dev-local-storage-signing-key";
}
