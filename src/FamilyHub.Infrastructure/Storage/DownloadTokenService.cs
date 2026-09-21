using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Storage;

/// <summary>Настройки выдачи ссылок на скачивание вложений (секция "Attachments").</summary>
public class AttachmentDownloadOptions
{
    public const string SectionName = "Attachments";

    /// <summary>Секрет HMAC-подписи ссылок. Генерация: openssl rand -base64 32. Активный —
    /// им подписываются НОВЫЕ ссылки.</summary>
    public string DownloadSigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Отставные ключи подписи (ADR-0009) — принимаются только на ПРОВЕРКУ уже выданных ссылок,
    /// никогда не используются для подписи новых. Ссылка живёт всего <see cref="UrlTtl"/>
    /// (по умолчанию 5 минут) — в отличие от связок Encryption/Jwt, здесь нет отдельного keyId
    /// (подпись не несёт его), проверка просто перебирает связку целиком; отставной ключ можно
    /// убирать из конфигурации почти сразу после ротации, как только истекут ссылки, выданные
    /// до нее. Env: <c>Attachments__PreviousSigningKeys__0</c>, <c>__1</c>, ...
    /// </summary>
    public List<string> PreviousSigningKeys { get; set; } = [];

    /// <summary>Время жизни ссылки на скачивание.</summary>
    public TimeSpan UrlTtl { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Fail-fast при старте хоста (cleanup-рефакторинг — заменяет прежнюю ручную проверку сырой
/// строки конфига в AddFamilyHubEncryption) — без него DownloadTokenService.RequireKey (ниже)
/// бросал бы лениво, только при первой попытке выдать ссылку (см. находку 09.2 аудита
/// безопасности). RequireKey остаётся как есть — дешёвая защита на случай, если
/// AttachmentDownloadOptions когда-нибудь будет сконструирован в обход обычной DI-валидации
/// (например, напрямую в тесте).
/// </summary>
public class AttachmentDownloadOptionsValidator : IValidateOptions<AttachmentDownloadOptions>
{
    public ValidateOptionsResult Validate(string? name, AttachmentDownloadOptions options) =>
        string.IsNullOrWhiteSpace(options.DownloadSigningKey)
            ? ValidateOptionsResult.Fail(
                "Attachments:DownloadSigningKey не задан (env Attachments__DownloadSigningKey) — " +
                "выдача ссылок на вложения невозможна.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// Что именно ссылка разрешает получить — примешивается в подписываемый payload, чтобы ссылка,
/// выданная на миниатюру, не могла быть подставлена в эндпоинт оригинала (и наоборот). До
/// добавления превью (см. AttachmentPreview) существовал только один эндпоинт /file, поэтому
/// в scope не было нужды — теперь их четыре, каждый со своей раздачей содержимого.
/// </summary>
public enum DownloadScope
{
    /// <summary>Оригинал вложения — GET /api/attachments/{id}/file (скачивание, Content-Disposition: attachment).</summary>
    File,

    /// <summary>Оригинал, отдаваемый inline (без Content-Disposition) — GET /api/attachments/{id}/inline, для текстовых превью.</summary>
    Inline,

    /// <summary>Миниатюра превью — GET /api/attachments/{id}/preview/thumb.</summary>
    Thumbnail,

    /// <summary>PDF-артефакт превью — результат конвертации Office-документа — GET /api/attachments/{id}/preview/pdf.</summary>
    PreviewPdf,

    /// <summary>Нормализованная полная картинка (HEIC/TIFF → JPEG) — GET /api/attachments/{id}/preview/page.</summary>
    PreviewPage,
}

/// <summary>
/// Короткоживущие подписанные ссылки на скачивание вложений через собственный API-эндпоинт
/// (замена presigned URL хранилища: блобы зашифрованы, отдавать их напрямую бессмысленно).
/// Авторизация происходит в момент ВЫДАЧИ ссылки (проверка видимости записи);
/// сам эндпоинт проверяет только подпись, срок и совпадение scope — как и у presigned URL раньше.
/// </summary>
public class DownloadTokenService(IOptions<AttachmentDownloadOptions> options)
{
    private static readonly Dictionary<DownloadScope, string> RoutesByScope = new()
    {
        [DownloadScope.File] = "file",
        [DownloadScope.Inline] = "inline",
        [DownloadScope.Thumbnail] = "preview/thumb",
        [DownloadScope.PreviewPdf] = "preview/pdf",
        [DownloadScope.PreviewPage] = "preview/page",
    };

    public string CreateUrl(Guid attachmentId, DownloadScope scope = DownloadScope.File)
    {
        var expiresAtUnix = DateTimeOffset.UtcNow.Add(options.Value.UrlTtl).ToUnixTimeSeconds();
        var signature = Sign(RequireKey(options.Value.DownloadSigningKey, "Attachments:DownloadSigningKey"), attachmentId, scope, expiresAtUnix);
        return $"/api/attachments/{attachmentId}/{RoutesByScope[scope]}?expires={expiresAtUnix}&sig={signature}";
    }

    public bool Validate(Guid attachmentId, DownloadScope scope, long expiresAtUnix, string signature)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
            return false;

        var signatureBytes = Encoding.UTF8.GetBytes(signature);

        // Перебор связки (ADR-0009): активный ключ первым (частый случай — без ротации),
        // затем отставные — ссылка, подписанная до ротации, остаётся валидна до истечения TTL.
        foreach (var key in AllKeys())
        {
            var expected = Sign(key, attachmentId, scope, expiresAtUnix);
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), signatureBytes))
                return true;
        }
        return false;
    }

    private IEnumerable<string> AllKeys()
    {
        yield return RequireKey(options.Value.DownloadSigningKey, "Attachments:DownloadSigningKey");
        foreach (var key in options.Value.PreviousSigningKeys)
        {
            if (!string.IsNullOrWhiteSpace(key)) yield return key;
        }
    }

    private static string RequireKey(string key, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"{sourceName} не задан — выдача ссылок невозможна.");
        return key;
    }

    private static string Sign(string key, Guid attachmentId, DownloadScope scope, long expiresAtUnix)
    {
        var payload = $"{attachmentId}:{scope}:{expiresAtUnix}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }
}
