namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Ключ объекта не несёт НИКАКОЙ семантики: ни типа записи, ни владельца, ни группировки сканов
/// по одной медкарте. Администратор хранилища и его служебные логи видят только набор несвязанных
/// шифроблобов — связь blob ↔ запись живёт единственно в medical."FileAttachments"."StorageKey".
/// Двухуровневый шард по первым байтам случайного GUID — чтобы листинг бакета не деградировал.
/// </summary>
public static class StorageKeyFactory
{
    public static string Create(Guid attachmentId)
    {
        var hex = attachmentId.ToString("N");
        return $"blobs/{hex[..2]}/{hex[2..4]}/{hex}";
    }

    /// <summary>Отдельное пространство ключей ("previews/", не "blobs/") — превью-блобы удаляются
    /// и пересоздаются независимо от оригинала (регенерируемый кэш, см. AttachmentPreview),
    /// префикс сразу отличает их в листинге бакета (админ-статистика, ADR-0009).</summary>
    public static string CreatePreviewKey(Guid previewId)
    {
        var hex = previewId.ToString("N");
        return $"previews/{hex[..2]}/{hex[2..4]}/{hex}";
    }
}
