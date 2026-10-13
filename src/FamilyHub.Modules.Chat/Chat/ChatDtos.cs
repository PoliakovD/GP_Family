using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Chat.Chat;

public enum ChatResult { Success, NotFound, Forbidden, Invalid }

/// <summary>ФИО автора частями — фронт форматирует их сам под ширину экрана (person-name/avatar).
/// Автор мог уже выйти из семьи, поэтому имя едет в сообщении, а не берётся из списка участников.</summary>
public record ChatAuthorDto(Guid UserId, string? LastName, string? FirstName, string? MiddleName);

public record ChatAttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, AttachmentPreviewStatus PreviewStatus);

public record ChatReactionDto(string Emoji, int Count, bool Mine, List<Guid> UserIds);

public record ChatLinkDto(
    ChatLinkKind Kind, Guid EntityId, bool Accessible, string? Title, string? Subtitle, NotificationRelatedKind? RouteKind);

/// <summary>Цитата сообщения, на которое ответили: начало текста (до 140 символов) и автор.</summary>
public record ChatReplyPreviewDto(Guid Id, ChatAuthorDto Author, string Snippet, bool Deleted, int AttachmentCount);

public record ChatMessageDto(
    Guid Id,
    Guid FamilyId,
    ChatAuthorDto Author,
    string Text,
    DateTime CreatedAt,
    DateTime? EditedAt,
    bool Deleted,
    ChatReplyPreviewDto? ReplyTo,
    List<ChatAttachmentDto> Attachments,
    List<ChatReactionDto> Reactions,
    List<ChatLinkDto> Links,
    bool CanEdit,
    bool CanDelete);

/// <summary>Страница ленты — от новых к старым. <see cref="NextCursor"/> — передать как <c>before</c> за
/// следующей (более старой) страницей; null — история кончилась.</summary>
public record ChatPageDto(List<ChatMessageDto> Items, string? NextCursor);

public record ChatLinkRequest(ChatLinkKind Kind, Guid EntityId);

/// <summary>Файл отправляемого сообщения — без привязки к IFormFile, чтобы сервис тестировался без HTTP.</summary>
public record ChatUpload(string FileName, string ContentType, long SizeBytes, Func<Stream> OpenRead);

public record SendChatMessageInput(string? Text, Guid? ReplyToMessageId, IReadOnlyList<ChatLinkRequest> Links, IReadOnlyList<ChatUpload> Files);

public record EditChatMessageRequest(string Text);

public record MarkChatReadRequest(DateTime? UpTo);

public record ChatUnreadDto(Guid FamilyId, int Count);

public record ChatConfigDto(int MaxTextLength, int MaxFilesPerMessage, long MaxFileSizeBytes, int MaxLinksPerMessage, IReadOnlyList<string> Reactions, IReadOnlyCollection<string> AllowedContentTypes);

public record HubTicketDto(string Ticket, DateTime ExpiresAt);
