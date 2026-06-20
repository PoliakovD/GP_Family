using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.TestUtils;

/// <summary>Простая заглушка ICurrentUser для случаев, где код зависит от интерфейса, а не от Guid-параметра напрямую.</summary>
public sealed class FakeCurrentUser(Guid userId, long telegramId = 1) : ICurrentUser
{
    public Guid UserId { get; } = userId;

    public long TelegramId { get; } = telegramId;
}
