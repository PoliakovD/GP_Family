namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Санитизация пользовательского имени файла (из IFormFile.FileName — клиентский
/// Content-Disposition, ASP.NET Core его НЕ проверяет на путевые последовательности).
/// Значение хранится как метаданные (FileAttachment.FileName) и позже используется как часть
/// имени записи в ZIP-экспорте (AccountService.BuildZipAsync) — без санитизации это классический
/// Zip Slip вектор (см. аудит module-review-2026-08-02, находка 1). На диске путь и так безопасен
/// (используется attachmentId, не имя файла — см. AttachmentService.UploadForMedicalRecordAsync),
/// это про метаданные, которые позже становятся частью пути где-то ещё.
/// </summary>
public static class FileNameSanitizer
{
    private const string FallbackName = "file";
    private const int MaxLength = 255;

    public static string Sanitize(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName)) return FallbackName;

        // Оба разделителя пути — клиент может прислать любой независимо от ОС сервера
        // (Path.GetFileName учитывает только DirectorySeparatorChar текущей ОС — этого мало).
        var name = rawFileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];

        // Управляющие символы (включая null-байт) — не валидны в именах файлов большинства ФС,
        // но сами по себе через API не отфильтровываются.
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());

        // Ведущие/замыкающие точки — после отсечения пути выше "имя" вроде ".." само по себе
        // остаться не должно, но подчищаем явно (defense in depth).
        name = name.Trim().Trim('.');

        if (name.Length > MaxLength) name = name[..MaxLength];

        return string.IsNullOrEmpty(name) ? FallbackName : name;
    }
}
