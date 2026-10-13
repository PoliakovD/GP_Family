using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Chat;

/// <summary>Карточка ссылки из сообщения чата — уже под конкретного зрителя. <see cref="Accessible"/> = false:
/// сущности нет или у зрителя к ней нет доступа — тогда ни заголовка, ни подзаголовка (ADR-0019 п. 5).
/// <see cref="RouteKind"/> — куда вести по клику (тот же словарь маршрутов, что у уведомлений).</summary>
public record ChatLinkPreview(
    ChatLinkKind Kind,
    Guid EntityId,
    bool Accessible,
    string? Title = null,
    string? Subtitle = null,
    NotificationRelatedKind? RouteKind = null)
{
    public static ChatLinkPreview Unavailable(ChatLinkKind kind, Guid entityId) => new(kind, entityId, false);
}

/// <summary>
/// Разрешение ссылок чата в карточки. Чат (Modules.Chat) не знает о медицинских сущностях и их правилах
/// видимости — реализация живёт в модуле-владельце (Modules.Medical) и подключается через DI, как
/// IBirthdaySearchSource: модули друг на друга не ссылаются.
/// </summary>
public interface IChatLinkResolver
{
    bool CanResolve(ChatLinkKind kind);

    Task<ChatLinkPreview> ResolveAsync(ChatLinkKind kind, Guid entityId, Guid viewerUserId, CancellationToken ct = default);
}
