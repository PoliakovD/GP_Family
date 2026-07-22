using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Локальная реализация IFileStorage — блобы на диске. С этапа 2 содержимое приходит уже
/// зашифрованным (IFileCipher в AttachmentService), отдача — только через API-эндпоинт
/// с расшифровкой; presigned-ссылок и прямых маршрутов к файлам больше нет.
/// </summary>
public class LocalFileStorage(IOptions<LocalFileStorageOptions> options) : IFileStorage
{
    public async Task<string> SaveAsync(string storageKey, Stream content, long size, string contentType, CancellationToken ct = default)
    {
        // size не нужен для записи на диск (используется только реальным объектным стором,
        // которому заранее требуется длина потока) — игнорируем.
        var fullPath = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file, ct);

        return storageKey;
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Блоб {storageKey} отсутствует в локальном хранилище.", fullPath);

        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public string ResolvePath(string storageKey) =>
        Path.Combine(Path.GetFullPath(options.Value.RootPath), storageKey.Replace('/', Path.DirectorySeparatorChar));
}
