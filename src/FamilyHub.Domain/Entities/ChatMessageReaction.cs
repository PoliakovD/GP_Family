namespace FamilyHub.Domain.Entities;

/// <summary>Реакция участника на сообщение чата — один эмодзи из фиксированного набора; один человек может
/// поставить несколько разных, но каждый — один раз (UNIQUE на тройку).</summary>
public class ChatMessageReaction
{
    public Guid MessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;

    public Guid UserId { get; set; }

    public string Emoji { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
