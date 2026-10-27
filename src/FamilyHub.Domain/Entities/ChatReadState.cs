namespace FamilyHub.Domain.Entities;

/// <summary>До какого момента участник прочитал чат семьи — из этого считается счётчик непрочитанного.
/// Строки нет — не читал ни разу (непрочитанным считается всё, кроме своих сообщений).</summary>
public class ChatReadState
{
    public Guid FamilyId { get; set; }

    public Guid UserId { get; set; }

    public DateTime LastReadAt { get; set; }
}
