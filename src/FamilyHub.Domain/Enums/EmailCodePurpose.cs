namespace FamilyHub.Domain.Enums;

/// <summary>Назначение одноразового email-кода (этап 2 п.2.4).</summary>
public enum EmailCodePurpose
{
    /// <summary>Регистрация нового PWA-аккаунта.</summary>
    Register = 0,

    /// <summary>Привязка email к существующему (Telegram) аккаунту.</summary>
    LinkEmail = 1,
}
