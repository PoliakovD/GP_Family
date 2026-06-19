namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Абстракция объектного хранилища сканов. Сейчас — LocalFileStorage (диск), позже —
/// MinIO-клиент (этап 2 п.9 брифа), без изменения вызывающего кода.
/// Доступ к файлу — только через короткоживущий pre-signed URL, никаких постоянных
/// прямых ссылок (раздел 9 брифа).
/// </summary>
public interface IFileStorage
{
    /// <summary>Сохраняет содержимое под заданным ключом. Возвращает фактический storageKey.</summary>
    Task<string> SaveAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Короткоживущая ссылка на файл (минуты), действительная до истечения <paramref name="expiry"/>.</summary>
    Task<string> GetPresignedUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct = default);
}
