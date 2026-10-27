namespace FamilyHub.Modules.Chat.Chat;

/// <summary>Лимиты чата семьи (секция "Chat", env <c>Chat__MaxFilesPerMessage</c> и т.п.).</summary>
public class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>Максимальная длина текста одного сообщения (символов).</summary>
    public int MaxTextLength { get; set; } = 4000;

    /// <summary>Вложений в одном сообщении. Все файлы уходят одним multipart-запросом — лимит тела запроса
    /// эндпоинта отправки считается из этих двух значений (см. ChatEndpoints), а не из глобального лимита Kestrel.</summary>
    public int MaxFilesPerMessage { get; set; } = 6;

    /// <summary>Размер одного вложения. Фото фронт сжимает до отправки (image-compression).</summary>
    public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Ссылок на сущности в одном сообщении.</summary>
    public int MaxLinksPerMessage { get; set; } = 5;

    /// <summary>Размер страницы ленты по умолчанию и максимум, который можно запросить.</summary>
    public int DefaultPageSize { get; set; } = 40;
    public int MaxPageSize { get; set; } = 100;

    /// <summary>Не чаще одного уведомления о чате на получателя за это окно (DedupKey по окну, ADR-0019 п. 7).</summary>
    public TimeSpan NotificationWindow { get; set; } = TimeSpan.FromMinutes(10);
}
