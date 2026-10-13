using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Ссылка из сообщения чата на сущность приложения (ADR-0019 п. 5). Хранится только (Kind, EntityId) —
/// без снимка заголовка: сущность может быть персональной (мед-запись, курс), и заголовок разрешается при
/// каждом чтении под конкретного зрителя.
/// </summary>
public class ChatMessageLink
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;

    public ChatLinkKind Kind { get; set; }

    public Guid EntityId { get; set; }
}
