namespace FamilyHub.Infrastructure.Security.Credentials;

public enum CredentialAdminError
{
    /// <summary>Операция запрещена: чужая роль/учётка (Postgres 42501).</summary>
    Forbidden,

    /// <summary>Роль сейчас используется этим же подключением (Postgres 55006) — отключить нельзя.</summary>
    InUse,

    /// <summary>Передан не SCRAM-верификатор, а произвольная строка (Postgres 22023).</summary>
    InvalidVerifier,

    /// <summary>Хранилище недоступно (сеть, таймаут, 5xx).</summary>
    Unavailable,

    /// <summary>Хранилище отклонило запрос (4xx кроме «уже нет»).</summary>
    Rejected,
}

/// <summary>Ошибка операции управления учётками приложения (ADR-0011). Сообщение НИКОГДА не содержит
/// пароль/secret key — только код, статус и безопасный фрагмент ответа сервера.</summary>
public class CredentialAdminException(CredentialAdminError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public CredentialAdminError Error { get; } = error;
}
