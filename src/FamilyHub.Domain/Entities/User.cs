namespace FamilyHub.Domain.Entities;

/// <summary>Пользователь. Авторизуется через Telegram (TelegramId), может состоять в нескольких семьях.</summary>
public class User
{
    public Guid Id { get; set; }

    /// <summary>Telegram user id — основа авторизации.</summary>
    public long TelegramId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public List<FamilyMember> Memberships { get; set; } = [];
}
