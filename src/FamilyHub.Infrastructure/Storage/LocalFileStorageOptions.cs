namespace FamilyHub.Infrastructure.Storage;

/// <summary>Конфигурация секции "LocalFileStorage" в appsettings.</summary>
public class LocalFileStorageOptions
{
    public const string SectionName = "LocalFileStorage";

    /// <summary>Каталог на диске, куда складываются файлы (временная замена MinIO).</summary>
    public string RootPath { get; set; } = "App_Data/uploads";
}
