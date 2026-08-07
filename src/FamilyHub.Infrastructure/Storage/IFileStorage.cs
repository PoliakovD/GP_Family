namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Абстракция объектного хранилища сканов. Единственная реализация — MinioFileStorage, в т.ч.
/// в Development (LocalFileStorage упразднён). С этапа 2 блобы зашифрованы IFileCipher, поэтому
/// прямых/presigned-ссылок на хранилище больше нет: скачивание — только через API-эндпоинт,
/// который расшифровывает поток (доступ по короткоживущему HMAC-токену, см. DownloadTokenService).
/// </summary>
public interface IFileStorage
{
    /// <summary>Сохраняет содержимое под заданным ключом. Возвращает фактический storageKey.</summary>
    Task<string> SaveAsync(string storageKey, Stream content, long size, string contentType, CancellationToken ct = default);

    /// <summary>Открывает блоб на чтение (как записан — т.е. шифротекст для новых файлов).</summary>
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default);

    /// <summary>Удаляет блоб; отсутствие объекта не считается ошибкой (идемпотентно — для erasure).</summary>
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}
