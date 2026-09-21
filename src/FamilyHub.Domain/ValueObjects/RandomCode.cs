using System.Security.Cryptography;

namespace FamilyHub.Domain.ValueObjects;

/// <summary>
/// Генератор непредсказуемого кода общего назначения (инвайт-коды, одноразовые
/// Telegram-link-коды и т.п.) — раньше формула `Convert.ToHexStringLower(RandomNumberGenerator.
/// GetBytes(16))` была продублирована в InviteService и TelegramLinkService по отдельности.
/// Криптографически стойкий источник байт (RandomNumberGenerator), не Random/Guid — код
/// используется как секрет (предъявляется как токен доступа), а не просто как уникальный
/// идентификатор.
/// </summary>
public static class RandomCode
{
    /// <summary>32 hex-символа (16 случайных байт) — текущая длина обоих прежних мест
    /// использования. Параметризуемо на случай, если будущему потребителю понадобится другая
    /// длина, но дефолт сохраняет обратную совместимость с уже выданными кодами по формату.</summary>
    public static string Generate(int byteLength = 16) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteLength));
}
