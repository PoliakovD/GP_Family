namespace FamilyHub.Domain.Entities;

/// <summary>
/// Сообщение общего чата семьи (ADR-0019) — семейный ресурс: читают и пишут активные члены семьи,
/// правит только автор, удаляет автор или админ семьи. Один чат на семью — отдельной сущности «чат» нет,
/// лента = все сообщения с этим FamilyId.
/// </summary>
public class ChatMessage : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    /// <summary>FK на User с каскадом: удаление аккаунта уносит и его сообщения (право на забвение).</summary>
    public Guid AuthorUserId { get; set; }
    public User Author { get; set; } = null!;

    /// <summary>Пустая строка — сообщение только из вложений/ссылок. Чат семьи на практике про здоровье,
    /// поэтому шифруется (ADR-0002); следствие — серверного поиска по чату нет.</summary>
    [Encrypted]
    public string Text { get; set; } = string.Empty;

    /// <summary>Ответ на сообщение той же семьи; при удалении исходного (вместе с аккаунтом) — null.</summary>
    public Guid? ReplyToMessageId { get; set; }
    public ChatMessage? ReplyTo { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? EditedAt { get; set; }

    /// <summary>Мягкое удаление: текст, ссылки, реакции и вложения стёрты, строка остаётся, чтобы ответы
    /// на сообщение не повисали («Сообщение удалено»).</summary>
    public DateTime? DeletedAt { get; set; }

    public List<ChatMessageReaction> Reactions { get; set; } = [];

    public List<ChatMessageLink> Links { get; set; } = [];
}
