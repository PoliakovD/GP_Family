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

    /// <summary>
    /// Перечисляет объекты бакета (ключ + размер) — только для админ-статистики (ADR-0009,
    /// сверка занятого места и осиротевших блобов). Не используется в обычном прикладном пути:
    /// ключи хранилища принципиально непрозрачны (StorageKeyFactory), листинг сам по себе не
    /// раскрывает содержимого/владельца, но перечисление всего бакета — дорогая операция, вызывать
    /// только из редко дёргаемой статистики, не из горячего пути запроса.
    /// </summary>
    IAsyncEnumerable<StorageObjectInfo> ListAsync(CancellationToken ct = default);
}

/// <summary>Один объект бакета, как его видит листинг (ADR-0009) — без содержимого.</summary>
public record StorageObjectInfo(string Key, long SizeBytes);
